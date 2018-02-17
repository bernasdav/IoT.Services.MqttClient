﻿using Autofac;
using IoT.Services.EventBus.Events;
using IoT.Services.EventBus.RabbitMQ;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace IoT.Services.EventBus
{
    public class EventBusService : IEventBus, IDisposable
    {
        const string brokerName = "IotEventBus";

        private readonly IRabbitMQPersistentConnection persistentConnection;
        private readonly IEventBusSubscriptionsManager subsManager;
        private readonly int retryCount;
        private IModel consumerChannel;
        private string queueName;
        private Dictionary<string, IContainer> containers;

        public EventBusService()
        {
            persistentConnection = new DefaultRabbitMQPersistentConnection(new ConnectionFactory { HostName = "localhost" });
            subsManager = new InMemoryEventBusSubscriptionsManager();
            queueName = brokerName;
            consumerChannel = CreateConsumerChannel();
            containers = new Dictionary<string, IContainer>();
            retryCount = 5;
            subsManager.OnEventRemoved += SubsManagerOnEventRemoved;
        }

        private void SubsManagerOnEventRemoved(object sender, string eventName)
        {
            if (!persistentConnection.IsConnected)
            {
                persistentConnection.TryConnect();
            }

            using (var channel = persistentConnection.CreateModel())
            {
                channel.QueueUnbind(queue: queueName,
                    exchange: brokerName,
                    routingKey: eventName);

                if (subsManager.IsEmpty)
                {
                    queueName = string.Empty;
                    consumerChannel.Close();
                }
            }
        }

        public void Publish(IntegrationEvent @event)
        {
            if (!persistentConnection.IsConnected)
            {
                persistentConnection.TryConnect();
            }

            var policy = RetryPolicy.Handle<BrokerUnreachableException>()
                .Or<SocketException>()
                .WaitAndRetry(retryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                {
                    //logger.LogWarning(ex.ToString());
                });

            using (var channel = persistentConnection.CreateModel())
            {
                var eventName = @event.GetType()
                    .Name;

                channel.ExchangeDeclare(exchange: brokerName,
                                    type: "direct");

                var message = JsonConvert.SerializeObject(@event);
                var body = Encoding.UTF8.GetBytes(message);

                policy.Execute(() =>
                {
                    channel.BasicPublish(exchange: brokerName,
                                     routingKey: eventName,
                                     basicProperties: null,
                                     body: body);
                });
            }
        }

        public void SubscribeDynamic<TH>(string eventName)
            where TH : IDynamicIntegrationEventHandler
        {
            DoInternalSubscription(eventName);
            subsManager.AddDynamicSubscription<TH>(eventName);
        }

        public void Subscribe<T, TH>()
            where T : IntegrationEvent
            where TH : IIntegrationEventHandler<T>
        {
            var eventName = subsManager.GetEventKey<T>();
            DoInternalSubscription(eventName);
            subsManager.AddSubscription<T, TH>();
            AddContainer<T, TH>();
        }

        private void AddContainer<T, TH>()
            where T : IntegrationEvent
            where TH : IIntegrationEventHandler<T>
        {
            var eventName = subsManager.GetEventKey<T>();
            if (!containers.ContainsKey(eventName))
            {
                var b = new ContainerBuilder();
                b.RegisterType<TH>();
                IContainer container = b.Build();
                containers.Add(eventName, container);
            }
        }

        private void RemoveContainer(string eventName)
        {
            if (containers.ContainsKey(eventName))
            {
                containers.Remove(eventName);
            }
        }

        private void DoInternalSubscription(string eventName)
        {
            var containsKey = subsManager.HasSubscriptionsForEvent(eventName);
            if (!containsKey)
            {
                if (!persistentConnection.IsConnected)
                {
                    persistentConnection.TryConnect();
                }

                using (var channel = persistentConnection.CreateModel())
                {
                    channel.QueueBind(queue: queueName,
                                      exchange: brokerName,
                                      routingKey: eventName);
                }
            }
        }

        public void Unsubscribe<T, TH>()
            where TH : IIntegrationEventHandler<T>
            where T : IntegrationEvent
        {
            subsManager.RemoveSubscription<T, TH>();
            RemoveContainer(subsManager.GetEventKey<T>());
        }

        public void UnsubscribeDynamic<TH>(string eventName)
            where TH : IDynamicIntegrationEventHandler
        {
            subsManager.RemoveDynamicSubscription<TH>(eventName);
        }

        public void Dispose()
        {
            if (consumerChannel != null)
            {
                consumerChannel.Dispose();
            }

            subsManager.Clear();
        }

        private IModel CreateConsumerChannel()
        {
            if (!persistentConnection.IsConnected)
            {
                persistentConnection.TryConnect();
            }

            var channel = persistentConnection.CreateModel();

            channel.ExchangeDeclare(exchange: brokerName,
                                 type: "direct");

            channel.QueueDeclare(queue: queueName,
                                 durable: true,
                                 exclusive: false,
                                 autoDelete: false,
                                 arguments: null);


            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += async (model, ea) =>
            {
                var eventName = ea.RoutingKey;
                var message = Encoding.UTF8.GetString(ea.Body);

                await ProcessEvent(eventName, message);
            };

            channel.BasicConsume(queue: queueName,
                                 autoAck: false,
                                 consumer: consumer);

            channel.CallbackException += (sender, ea) =>
            {
                consumerChannel.Dispose();
                consumerChannel = CreateConsumerChannel();
            };

            return channel;
        }

        private async Task ProcessEvent(string eventName, string message)
        {
            if (subsManager.HasSubscriptionsForEvent(eventName))
            {
                var subscriptions = subsManager.GetHandlersForEvent(eventName);
                foreach (var subscription in subscriptions)
                {
                    var eventType = subsManager.GetEventTypeByName(eventName);
                    var concreteType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
                    var integrationEvent = JsonConvert.DeserializeObject(message, eventType);
                    if (containers.ContainsKey(eventName))
                    {
                        var handlerContainer = containers[eventName];
                        using (var handlerScope = handlerContainer.BeginLifetimeScope())
                        {
                            var handler = handlerScope.ResolveOptional(subscription.HandlerType);
                            await (Task)concreteType.GetMethod("Handle").Invoke(handler, new object[] { integrationEvent });
                        }
                    }
                }
            }
        }
    }
}
