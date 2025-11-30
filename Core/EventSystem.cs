using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core;
public class EventBus
{
    /// <summary>
    /// Internal interface for type-erased event queue storage
    /// Virtual method calls don't box value types
    /// </summary>
    private interface IEventQueue
    {
        int ProcessEvents();
        void Clear();
        int Count { get; }
    }

    /// <summary>
    /// Base interface for all game events
    /// </summary>
    public interface IGameEvent
    {
        Int64 TimeStamp { get; set; }
    }

    private class EventQueue<T> : IEventQueue where T : struct, IGameEvent
    {
        private const int INITIAL_QUEUE_CAPACITY = 256;

        private readonly Queue<T> eventQueue;
        private readonly Queue<T> processingQueue;
        private Action<T>? listeners;

        public int Count => eventQueue.Count;

        public EventQueue()
        {
            eventQueue = new Queue<T>(INITIAL_QUEUE_CAPACITY);
            processingQueue = new Queue<T>(INITIAL_QUEUE_CAPACITY);
        }

        public void Enqueue(T gameEvent)
        {
            eventQueue.Enqueue(gameEvent);  
        }

        public void AddListener(Action<T> handler)
        {
            listeners = (Action<T>)Delegate.Combine(listeners, handler);
        }

        public void RemoveListener(Action<T> handler)
        {
            if (listeners != null)
            {
                listeners = (Action<T>?)Delegate.Remove(listeners, handler);
            }
        }

        public int ProcessEvents()
        {
            if (eventQueue.Count == 0)
                return 0;

            int processed = 0;

            // Swap queues to allow new events during processing
            while (eventQueue.Count > 0)
            {
                processingQueue.Enqueue(eventQueue.Dequeue());  // NO BOXING - T to T
            }

            // Process all events
            while (processingQueue.Count > 0)
            {
                var gameEvent = processingQueue.Dequeue();  // NO BOXING - T stays T

                try
                {
                    // Invoke listeners - NO BOXING - direct Action<T> call
                    listeners?.Invoke(gameEvent);
                    processed++;
                }
                catch (Exception e)
                {
                    Logger.Error($"[Core] : Error processing event {typeof(T).Name}: {e.Message}\n{e.StackTrace}");
                }
            }

            return processed;
        }

        public void Clear()
        {
            eventQueue.Clear();
            processingQueue.Clear();
        }
    }

    private const int INITIAL_CAPACITY = 16;

    // Type-specific event queues - uses IEventQueue interface for polymorphism without boxing
    private readonly Dictionary<Type, IEventQueue> _eventQueues;

    // Performance monitoring
    private int _eventsProcessedThisFrame;
    private int _totalEventsProcessed;

    public bool IsActive { get; private set; }
    public int EventsProcessedTotal => _totalEventsProcessed;

    public int EventsInQueue
    {
        get
        {
            int total = 0;
            foreach (var queue in _eventQueues.Values)
            {
                total += queue.Count;
            }
            return total;
        }
    }

    public EventBus()
    {
        _eventQueues = new Dictionary<Type, IEventQueue>(INITIAL_CAPACITY);
        IsActive = true;
        Logger.Info("EventBus initialized");
    }

    /// <summary>
    /// Subscribe to events of a specific type
    /// </summary>
    public void Subscribe<T>(Action<T> handler) where T : struct, IGameEvent
    {
        var eventType = typeof(T);

        if (!_eventQueues.TryGetValue(eventType, out var queue))
        {
            queue = new EventQueue<T>();
            _eventQueues[eventType] = queue;
        }

        ((EventQueue<T>)queue).AddListener(handler);
    }

    /// <summary>
    /// Unsubscribe from events of a specific type
    /// </summary>
    public void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent
    {
        var eventType = typeof(T);

        if (!_eventQueues.TryGetValue(eventType, out var queue))
            return;

        ((EventQueue<T>)queue).RemoveListener(handler);
    }

    /// <summary>
    /// Emit an event - queued for frame-coherent processing
    /// </summary>
    public void Emit<T>(T gameEvent) where T : struct, IGameEvent
    {
        if (!IsActive)
            return;

        var eventType = typeof(T);

        // Set timestamp
        gameEvent.TimeStamp = TimeAgent.CurrentTime;

        // Get or create type-specific queue
        if (!_eventQueues.TryGetValue(eventType, out var queue))
        {
            queue = new EventQueue<T>();
            _eventQueues[eventType] = queue;
        }

        // Enqueue - NO BOXING (cast is safe, EventQueue<T> is what we created)
        ((EventQueue<T>)queue).Enqueue(gameEvent);
    }
}