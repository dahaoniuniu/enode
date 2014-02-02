﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ECommon.IoC;
using ECommon.Serializing;
using ECommon.Socketing;
using ECommon.Utilities;
using ENode.Commanding;
using ENode.Domain;
using ENode.Eventing;
using EQueue.Clients.Consumers;
using EQueue.Protocols;
using EQueue.Utils;

namespace ENode.EQueue
{
    public class CommandConsumer : IMessageHandler
    {
        private readonly Consumer _consumer;
        private readonly IBinarySerializer _binarySerializer;
        private readonly ICommandTypeCodeProvider _commandTypeCodeProvider;
        private readonly ICommandExecutor _commandExecutor;
        private readonly IRepository _repository;
        private readonly ConcurrentDictionary<Guid, IMessageContext> _messageContextDict;

        public Consumer Consumer { get { return _consumer; } }

        public CommandConsumer()
            : this(new ConsumerSetting())
        {
        }
        public CommandConsumer(string groupName)
            : this(new ConsumerSetting(), groupName)
        {
        }
        public CommandConsumer(ConsumerSetting setting)
            : this(setting, null)
        {
        }
        public CommandConsumer(ConsumerSetting setting, string groupName)
            : this(setting, null, groupName)
        {
        }
        public CommandConsumer(ConsumerSetting setting, string name, string groupName)
            : this(string.Format("{0}@{1}@{2}", SocketUtils.GetLocalIPV4(), string.IsNullOrEmpty(name) ? typeof(CommandConsumer).Name : name, ObjectId.GenerateNewId()), setting, groupName)
        {
        }
        public CommandConsumer(string id, ConsumerSetting setting, string groupName)
        {
            _consumer = new Consumer(id, setting, string.IsNullOrEmpty(groupName) ? typeof(CommandConsumer).Name + "Group" : groupName, this);
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _commandTypeCodeProvider = ObjectContainer.Resolve<ICommandTypeCodeProvider>();
            _commandExecutor = ObjectContainer.Resolve<ICommandExecutor>();
            _repository = ObjectContainer.Resolve<IRepository>();
            _messageContextDict = new ConcurrentDictionary<Guid, IMessageContext>();
        }

        public CommandConsumer Start()
        {
            _consumer.Start();
            return this;
        }
        public CommandConsumer Subscribe(string topic)
        {
            _consumer.Subscribe(topic);
            return this;
        }
        public CommandConsumer Shutdown()
        {
            _consumer.Shutdown();
            return this;
        }

        void IMessageHandler.Handle(QueueMessage message, IMessageContext context)
        {
            var payload = ByteTypeDataUtils.Decode(message.Body);
            var type = _commandTypeCodeProvider.GetType(payload.TypeCode);
            var command = _binarySerializer.Deserialize(payload.Data, type) as ICommand;

            if (_messageContextDict.TryAdd(command.Id, context))
            {
                _commandExecutor.Execute(command, new CommandExecuteContext(_repository, message, (executedCommand, queueMessage) =>
                {
                    IMessageContext messageContext;
                    if (_messageContextDict.TryRemove(executedCommand.Id, out messageContext))
                    {
                        messageContext.OnMessageHandled(queueMessage);
                    }
                }));
            }
        }

        class CommandExecuteContext : ICommandExecuteContext
        {
            private readonly ConcurrentDictionary<object, IAggregateRoot> _trackingAggregateRoots;
            private readonly IRepository _repository;

            public Action<ICommand, QueueMessage> CommandHandledAction { get; private set; }
            public QueueMessage QueueMessage { get; private set; }

            public CommandExecuteContext(IRepository repository, QueueMessage queueMessage, Action<ICommand, QueueMessage> commandHandledAction)
            {
                _trackingAggregateRoots = new ConcurrentDictionary<object, IAggregateRoot>();
                _repository = repository;
                QueueMessage = queueMessage;
                CheckCommandWaiting = true;
                CommandHandledAction = commandHandledAction;
            }

            public bool CheckCommandWaiting { get; set; }
            public void OnCommandExecuted(ICommand command)
            {
                CommandHandledAction(command, QueueMessage);
            }

            /// <summary>Add an aggregate root to the context.
            /// </summary>
            /// <param name="aggregateRoot">The aggregate root to add.</param>
            /// <exception cref="ArgumentNullException">Throwed when the aggregate root is null.</exception>
            public void Add(IAggregateRoot aggregateRoot)
            {
                if (aggregateRoot == null)
                {
                    throw new ArgumentNullException("aggregateRoot");
                }
                var firstSourcingEvent = aggregateRoot.GetUncommittedEvents().FirstOrDefault(x => x is ISourcingEvent);
                if (firstSourcingEvent == null)
                {
                    throw new NotSupportedException(string.Format("Aggregate [{0}] has no sourcing event, cannot be added.", aggregateRoot.GetType()));
                }

                _trackingAggregateRoots.TryAdd(firstSourcingEvent.AggregateRootId, aggregateRoot);
            }
            /// <summary>Get the aggregate from the context.
            /// </summary>
            /// <param name="id">The id of the aggregate root.</param>
            /// <typeparam name="T">The type of the aggregate root.</typeparam>
            /// <returns>The found aggregate root.</returns>
            /// <exception cref="ArgumentNullException">Throwed when the id is null.</exception>
            /// <exception cref="AggregateRootNotFoundException">Throwed when the aggregate root not found.</exception>
            public T Get<T>(object id) where T : class, IAggregateRoot
            {
                var aggregateRoot = GetOrDefault<T>(id);

                if (aggregateRoot == null)
                {
                    throw new AggregateRootNotFoundException(id, typeof(T));
                }

                return aggregateRoot;
            }
            /// <summary>Get the aggregate from the context, if the aggregate root not exist, returns null.
            /// </summary>
            /// <param name="id">The id of the aggregate root.</param>
            /// <typeparam name="T">The type of the aggregate root.</typeparam>
            /// <returns>If the aggregate root was found, then returns it; otherwise, returns null.</returns>
            /// <exception cref="ArgumentNullException">Throwed when the id is null.</exception>
            public T GetOrDefault<T>(object id) where T : class, IAggregateRoot
            {
                if (id == null)
                {
                    throw new ArgumentNullException("id");
                }

                var aggregateRoot = _repository.Get<T>(id);

                if (aggregateRoot != null)
                {
                    _trackingAggregateRoots.TryAdd(aggregateRoot.UniqueId, aggregateRoot);
                }

                return aggregateRoot;
            }
            /// <summary>Returns all the tracked aggregate roots of the current context.
            /// </summary>
            /// <returns></returns>
            public IEnumerable<IAggregateRoot> GetTrackedAggregateRoots()
            {
                return _trackingAggregateRoots.Values;
            }
            /// <summary>Clear all the tracking aggregates.
            /// </summary>
            public void Clear()
            {
                _trackingAggregateRoots.Clear();
            }
        }
    }
}
