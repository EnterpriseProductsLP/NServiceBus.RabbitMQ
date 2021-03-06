﻿namespace NServiceBus.Transport.RabbitMQ
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using global::RabbitMQ.Client.Events;
    using Janitor;
    using Performance.TimeToBeReceived;
    using Routing;
    using Settings;
    using Transports;

    [SkipWeaving]
    class RabbitMQTransportInfrastructure : TransportInfrastructure, IDisposable
    {
        readonly SettingsHolder settings;
        readonly ConnectionConfiguration connectionConfiguration;
        readonly ConnectionManager connectionManager;
        readonly ChannelProvider channelProvider;
        IRoutingTopology topology;

        public RabbitMQTransportInfrastructure(SettingsHolder settings, string connectionString)
        {
            this.settings = settings;

            connectionConfiguration = new ConnectionStringParser(settings).Parse(connectionString);
            connectionManager = new ConnectionManager(new ConnectionFactory(connectionConfiguration));
            channelProvider = new ChannelProvider(connectionManager, connectionConfiguration.UsePublisherConfirms);

            CreateTopology();

            RequireOutboxConsent = false;
        }

        public override IEnumerable<Type> DeliveryConstraints => new[] { typeof(DiscardIfNotReceivedBefore), typeof(NonDurableDelivery) };

        public override OutboundRoutingPolicy OutboundRoutingPolicy => new OutboundRoutingPolicy(OutboundRoutingType.Unicast, OutboundRoutingType.Multicast, OutboundRoutingType.Unicast);

        public override TransportTransactionMode TransactionMode => TransportTransactionMode.ReceiveOnly;

        public override EndpointInstance BindToLocalEndpoint(EndpointInstance instance) => instance;

        public override TransportReceiveInfrastructure ConfigureReceiveInfrastructure()
        {
            return new TransportReceiveInfrastructure(
                    () => CreateMessagePump(),
                    () => new QueueCreator(connectionManager, topology, settings.DurableMessagesEnabled()),
                    () => Task.FromResult(ObsoleteAppSettings.Check()));
        }

        public override TransportSendInfrastructure ConfigureSendInfrastructure()
        {
            return new TransportSendInfrastructure(
                () => new MessageDispatcher(topology, channelProvider),
                () => Task.FromResult(StartupCheckResult.Success));
        }

        public override TransportSubscriptionInfrastructure ConfigureSubscriptionInfrastructure()
        {
            return new TransportSubscriptionInfrastructure(() => new SubscriptionManager(connectionManager, topology, settings.LocalAddress()));
        }

        public override string ToTransportAddress(LogicalAddress logicalAddress)
        {
            var queue = new StringBuilder(logicalAddress.EndpointInstance.Endpoint.ToString());

            if (logicalAddress.EndpointInstance.Discriminator != null)
            {
                queue.Append("-" + logicalAddress.EndpointInstance.Discriminator);
            }

            if (logicalAddress.Qualifier != null)
            {
                queue.Append("." + logicalAddress.Qualifier);
            }

            return queue.ToString();
        }

        public void Dispose()
        {
            connectionManager.Dispose();
        }

        void CreateTopology()
        {
            if (settings.HasSetting<IRoutingTopology>())
            {
                topology = settings.Get<IRoutingTopology>();
            }
            else
            {
                var durable = settings.DurableMessagesEnabled();

                DirectRoutingTopology.Conventions conventions;

                if (settings.TryGet(out conventions))
                {
                    topology = new DirectRoutingTopology(conventions, durable);
                }
                else
                {
                    topology = new ConventionalRoutingTopology(durable);
                }
            }
        }

        IPushMessages CreateMessagePump()
        {
            MessageConverter messageConverter;

            if (settings.HasSetting(SettingsKeys.CustomMessageIdStrategy))
            {
                messageConverter = new MessageConverter(settings.Get<Func<BasicDeliverEventArgs, string>>(SettingsKeys.CustomMessageIdStrategy));
            }
            else
            {
                messageConverter = new MessageConverter();
            }

            string hostDisplayName;
            if (!settings.TryGet("NServiceBus.HostInformation.DisplayName", out hostDisplayName))
            {
                hostDisplayName = Support.RuntimeEnvironment.MachineName;
            }

            var consumerTag = $"{hostDisplayName} - {settings.EndpointName()}";

            var poisonMessageForwarder = new PoisonMessageForwarder(channelProvider, topology);

            var queuePurger = new QueuePurger(connectionManager);

            TimeSpan timeToWaitBeforeTriggeringCircuitBreaker;
            if (!settings.TryGet(SettingsKeys.TimeToWaitBeforeTriggeringCircuitBreaker, out timeToWaitBeforeTriggeringCircuitBreaker))
            {
                timeToWaitBeforeTriggeringCircuitBreaker = TimeSpan.FromMinutes(2);
            }

            return new MessagePump(connectionConfiguration, messageConverter, consumerTag, poisonMessageForwarder, queuePurger, timeToWaitBeforeTriggeringCircuitBreaker);
        }
    }
}
