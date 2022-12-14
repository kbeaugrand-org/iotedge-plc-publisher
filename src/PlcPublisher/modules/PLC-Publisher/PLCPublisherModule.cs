
namespace PLCPublisher
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Extensions.Logging;
    using PLCPublisher.Commands;
    using PLCPublisher.Commands.ListTags;
    using PLCPublisher.Commands.ListUdtTypes;
    using PLCPublisher.Commands.ReadTag;
    using PLCPublisher.Commands.ReadArray;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using PLCPublisher.TwinSettings;
    using System;
    using System.Collections.Concurrent;
    using PLCPublisher.Factories;
    using System.Text;
    using libplctag;
    using PLCPublisher.Commands.ListPrograms;
    using JUST;
    using System.Collections.Generic;
    using System.Linq;

    public class PLCPublisherModule : IHostedService
    {
        private readonly ModuleClient moduleClient;
        private readonly ILogger logger;
        private readonly ITagFactory tagFactory;
        private readonly ICommandHandler listTagsCommandHandler;
        private readonly ICommandHandler readTagCommandHandler;
        private readonly ICommandHandler readArrayCommandHandler;
        private readonly ICommandHandler listUdtTypesCommandHandler;
        private readonly ICommandHandler listProgramsCommandHandler;
        private readonly ConcurrentBag<object> runningTasks = new ConcurrentBag<object>();
        private readonly ConcurrentQueue<EnqueuedMessage> messagesQueue = new ConcurrentQueue<EnqueuedMessage>();
        private CancellationTokenSource runningTaskCancellationTokenSource;

        public PLCPublisherModule(
            ILogger<PLCPublisherModule> logger,
            ITagFactory tagFactory,
            ListTagsCommandHandler listTagsCommandHandler,
            ListUdtTypesCommandHandler listUdtTypesCommandHandler,
            ListProgramsCommandHandler listProgramsCommandHandler,
            ReadTagCommandHandler readTagCommandHandler,
            ReadArrayCommandHandler readArrayCommandHandler)
        {
            this.logger = logger;
            this.tagFactory = tagFactory;
            this.moduleClient = ModuleClient.CreateFromEnvironmentAsync().Result;

            this.listTagsCommandHandler = listTagsCommandHandler;
            this.listUdtTypesCommandHandler = listUdtTypesCommandHandler;
            this.listProgramsCommandHandler = listProgramsCommandHandler;

            this.readTagCommandHandler = readTagCommandHandler;
            this.readArrayCommandHandler = readArrayCommandHandler;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await moduleClient.OpenAsync();

            this.logger.LogInformation("ModuleClient initialized");

            var twin = await this.moduleClient.GetTwinAsync();

            await this.moduleClient.SetDesiredPropertyUpdateCallbackAsync(this.OnDesiredPropertyUpdate, null);

            await this.moduleClient.SetMethodHandlerAsync("ListTags", async (x, ctx) => await this.listTagsCommandHandler.Handle(x), null);
            await this.moduleClient.SetMethodHandlerAsync("ListUdtTypes", async (x, ctx) => await this.listUdtTypesCommandHandler.Handle(x), null);
            await this.moduleClient.SetMethodHandlerAsync("ListPrograms", async (x, ctx) => await this.listProgramsCommandHandler.Handle(x), null);
            await this.moduleClient.SetMethodHandlerAsync("ReadTag", async (x, ctx) => await this.readTagCommandHandler.Handle(x), null);
            await this.moduleClient.SetMethodHandlerAsync("ReadArray", async (x, ctx) => await this.readArrayCommandHandler.Handle(x), null);

            this.logger.LogInformation("Method handlers registered");

            ThreadPool.QueueUserWorkItem(async (state) => await SendBatchedEvent());
            this.logger.LogInformation("Publisher thread started");

            await RegisterTags(twin.Properties.Desired);

            this.logger.LogInformation("Tags registered");
        }

        private async Task OnDesiredPropertyUpdate(TwinCollection desiredProperties, object userContext)
        {
            this.logger.LogInformation("Desired property update received");

            await UnregisterTags();
            await RegisterTags(desiredProperties);
        }


        public class EnqueuedMessage
        {
            public DateTimeOffset EnqueuedTime { get; set; } = DateTimeOffset.UtcNow;

            public TagTwinDefinition Tag { get; set; }

            public object Data { get; set; }
        }

        private async Task SendBatchedEvent()
        {
            List<EnqueuedMessage> currentMessages = new List<EnqueuedMessage>();

            do
            {
                if (!this.messagesQueue.TryDequeue(out var item))
                {
                    break;
                }

                currentMessages.Add(item);
            } while (true);

            var messages = currentMessages.Select(item =>
            {
                var jsonTagValue = JsonConvert.SerializeObject(item.Data);
                this.logger.LogTrace($"handling message for tag {item.Tag.Name} with value {jsonTagValue}");

                JObject jsonEvent = null;

                if (item.Tag.Transform != null)
                {
                    this.logger.LogTrace("Transforming message with {0}", item.Tag.Transform.ToString());
                    JObject inputMessage = new JObject();
                    inputMessage.Add("input", JToken.FromObject(item.Data));

                    jsonEvent = JObject.Parse(JsonTransformer.Transform(item.Tag.Transform.ToString(), inputMessage.ToString()));
                }
                else
                {
                    jsonEvent = JObject.Parse($"{{\"{item.Tag.Name}\":{jsonTagValue}}}");
                }

                jsonEvent.Add("timestamp", item.EnqueuedTime.ToString("o"));
                var jsonData = jsonEvent.ToString();

                this.logger.LogTrace("{1}", jsonData);

                return new Message(Encoding.UTF8.GetBytes(jsonData))
                {
                    CreationTimeUtc = item.EnqueuedTime.UtcDateTime,
                };
            }).ToArray();

            if (!messages.Any())
                return;

            this.logger.LogDebug("Sending {0} messages", messages.Count());
            await this.moduleClient.SendEventBatchAsync("tag", messages);

            Thread.Sleep(100);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await UnregisterTags();
            await moduleClient.CloseAsync();
        }

        private Task RegisterTags(TwinCollection desiredProperties)
        {
            this.logger.LogInformation("Registering tags");

            if (this.runningTasks.Count > 0)
            {
                throw new InvalidProgramException("Should unregister polls before registering new ones");
            }

            if (runningTaskCancellationTokenSource != null && !runningTaskCancellationTokenSource.IsCancellationRequested)
            {
                throw new InvalidProgramException("Should unregister polls before registering new ones");
            }

            runningTaskCancellationTokenSource = new CancellationTokenSource();

            foreach (var item in desiredProperties["tags"])
            {
                var tagDefinition = JsonConvert.DeserializeObject<TagTwinDefinition>(item.ToString());

                StartTagPolling(tagDefinition, runningTaskCancellationTokenSource.Token);
            }

            return Task.CompletedTask;
        }

        private async Task UnregisterTags()
        {
            this.logger.LogInformation("Unregister tags");

            this.runningTaskCancellationTokenSource.Cancel();

            await Task.Factory.StartNew(() => { while (this.runningTasks.Count > 0) { } });
        }

        private void StartTagPolling(TagTwinDefinition tag, CancellationToken cancellationToken)
        {
            this.logger.LogDebug($"Tag: {tag.TagName}, PollingInterval: {tag.PollingInterval}");

            var thread = new Thread(async c =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tag.PollingInterval));
                this.runningTasks.Add(new object());
                this.logger.LogTrace($"Starting polling for tag {tag.TagName}");

                using var plcTag = this.tagFactory.Create(tag);
                plcTag.ReadCacheMillisecondDuration = 0;

                do
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(cancellationToken);

                        this.logger.LogTrace("Polling tag {0}", tag.TagName);
                        plcTag.Read();
                        this.logger.LogTrace("Polling tag {0} completed ({1})", tag.TagName, plcTag.GetStatus());

                        if (plcTag.GetStatus() != Status.Ok)
                        {
                            this.logger.LogWarning($"Tag {tag.TagName} is not OK - {plcTag.GetStatus()}");
                            continue;
                        }

                        this.messagesQueue.Enqueue(new EnqueuedMessage
                        {
                            Data = plcTag.Value,
                            Tag = tag
                        });

                        this.logger.LogTrace("Message queued for tag {0}", tag.TagName);
                    }
                    catch (Exception e)
                        when (e is TaskCanceledException || e is OperationCanceledException)
                    {
                        logger.LogInformation("Polling for tag {0} cancelled", tag.TagName);
                        return;
                    }
                    catch (Exception e)
                        when (e is LibPlcTagException)
                    {
                        this.logger.LogError(e, "Error while polling tag");
                        return;
                    }
                    finally
                    {
                        this.runningTasks.TryTake(out _);
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
            });

            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }
    }
}