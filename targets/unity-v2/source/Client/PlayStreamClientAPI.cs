#if !DISABLE_PLAYFABCLIENT_API
using System.Collections;
using System.Collections.Generic;
using PlayFab.ClientModels;
using PlayFab.Internal;
using UnityEngine;
using System;
using PlayFab.Json;

namespace PlayFab
{
    public static partial class PlayFabClientAPI
    {
        private enum QueueConnectionState
        {
            Finished, // Shutdown the coroutine because there's no work to do
            SingleSafe, // Send single events rather than batches, because of past errors, disconnects, or empty queue
            SingleError, // Trash the front event, and send the next single event, likely because of an exception on the local device, preventing normal operation
            Batch, // We're backlogged with events, previous events were successful, push events in batches
        }

        // Used by unit testing to determine that batches are actually happening as expected
        public delegate void DebugBatchProcessDelegate(int processed, int remaining);
        // Used by unit testing to determine that batches are actually happening as expected
        public static event DebugBatchProcessDelegate OnDebugBatchEvent;

        private const int EVENT_BATCH_COUNT = 50;
        private static readonly TimeSpan SINGLE_ERROR_TIMEOUT = TimeSpan.FromSeconds(60); // If an api-call does not succeed or fail within this timespan, we go into "critical meltdown" mode and start dropping events
        private static readonly TimeSpan SUCCESS_DELAY = TimeSpan.FromMilliseconds(2000); // Delay between successful PlayStream writes, to avoid over-spamming the server
        private static readonly TimeSpan BASE_RETRY_TIME = TimeSpan.FromSeconds(6); // On api-failure, we retry events until we succeed
        private const double MAX_RETRY_MULTIPLIER = 200; // ... until it gets just short of ridiculous
        private static readonly List<BatchedEvent> PendingEvents = new List<BatchedEvent>();

        private static System.Random _rng = new System.Random();
        private static double _retryMultiplier; // On api-failure, we retry events on a random-exponential growth curve ...
        private static QueueConnectionState _queueState = QueueConnectionState.Finished;
        private static DateTime _queueEventTimestamp = DateTime.UtcNow;
        private static bool _coroutineActive = false;

        private static WriteBatchEventRequest _batchRequest;

        private static void ActivateQueue()
        {
            // var path = Application.persistentDataPath; // Might need this later

            if (_coroutineActive)
                return;
            _coroutineActive = true;
            UpdateQueueState(false); // [Re]Starting thread, first call is always a single
            _queueEventTimestamp = DateTime.UtcNow; // Send an event immediately
            _batchRequest = new WriteBatchEventRequest { Events = new List<BatchedEvent>() };
            _retryMultiplier = 1;

            PlayFabHttp.instance.StartCoroutine(PlayStreamQueueMainLoop());
        }

        public static void UpdateQueueState(bool batchAllowed)
        {
            if (PendingEvents.Count == 0)
                _queueState = QueueConnectionState.Finished;
            else if (!batchAllowed)
                _queueState = QueueConnectionState.SingleSafe;
            else
                _queueState = QueueConnectionState.Batch;
            Debug.Log("UpdateQueueState: " + _queueState);
        }

        private static IEnumerator PlayStreamQueueMainLoop()
        {
            Debug.Log("PlayStreamQueueMainLoop starting");
            while (_queueState != QueueConnectionState.Finished)
            {
                if (DateTime.UtcNow > _queueEventTimestamp)
                {
                    switch (_queueState)
                    {
                        case QueueConnectionState.SingleSafe:
                            SendEventBatch(1);
                            break;
                        case QueueConnectionState.SingleError:
                            Debug.LogError("Trashing Queued PlayStream Event due to unhandled critical errors.");
                            PendingEvents.RemoveAt(0);
                            SendEventBatch(EVENT_BATCH_COUNT);
                            break;
                        case QueueConnectionState.Batch:
                            SendEventBatch();
                            break;
                    }
                }
                yield return 1; // No more work to process this tick
            }
            _coroutineActive = false;
            _batchRequest = null;
            Debug.Log("PlayStreamQueueMainLoop ending");
        }

        private static void SendEventBatch(int maxCount = 1)
        {
            _batchRequest.Events.Clear();
            for (var i = 0; i < maxCount && i < PendingEvents.Count; i++)
                _batchRequest.Events.Add(PendingEvents[i]);
            Debug.Log("SendEventBatch: " + _batchRequest.Events.Count);
            WriteEventBatch(_batchRequest, OnSuccess, OnError);

            // Temporary state until we get a callback, or worst-case this state times-out
            _queueState = QueueConnectionState.SingleError;
            _queueEventTimestamp = DateTime.UtcNow + SINGLE_ERROR_TIMEOUT;
        }

        private static void OnSuccess(EmptyResult result)
        {
            Debug.Log("SendEventBatch-OnSuccess");
            var eventList = ((WriteBatchEventRequest)result.Request).Events;
            foreach (var eachSuccess in eventList)
            {
                if (ReferenceEquals(PendingEvents[0], eachSuccess))
                {
                    PendingEvents.RemoveAt(0);
                }
                else if (PendingEvents.Contains(eachSuccess))
                {
                    PendingEvents.Remove(eachSuccess);
                    Debug.LogError("Critical Error: Events were posted out of order (this will cause event duplication).");
                }
                else
                {
                    Debug.LogError("Critical Error: Posted event doesn't match anything that is queued.");
                }
            }
            if (OnDebugBatchEvent != null)
                OnDebugBatchEvent(eventList.Count, PendingEvents.Count);
            UpdateQueueState(true); // After a successful event, we allow batching
            _queueEventTimestamp = DateTime.UtcNow + SUCCESS_DELAY; // Send more events asap
            _retryMultiplier = 1; // Success resets this multiplier
        }

        private static void OnError(PlayFabError error)
        {
            Debug.Log("SendEventBatch-OnError");
            // Queue up a retry after an random-exponential-wait
            UpdateQueueState(false);
            _queueEventTimestamp = DateTime.UtcNow + TimeSpan.FromSeconds(BASE_RETRY_TIME.TotalSeconds * _retryMultiplier);
            _retryMultiplier = Math.Min(MAX_RETRY_MULTIPLIER, _retryMultiplier * 10.0 * (0.9 + 0.2 * _rng.NextDouble()));
        }

        /// <summary>
        /// Writes a character-based event into PlayStream.
        /// </summary>
        public static void WriteCharacterEventQueued(WriteClientCharacterEventRequest request)
        {
            PendingEvents.Add(new BatchedEvent { EventType = PlayStreamEventType.CharacterEvent, EventJson = JsonWrapper.SerializeObject(request) });
            ActivateQueue();
        }

        /// <summary>
        /// Writes a player-based event into PlayStream.
        /// </summary>
        public static void WritePlayerEventQueued(WriteClientPlayerEventRequest request)
        {
            PendingEvents.Add(new BatchedEvent { EventType = PlayStreamEventType.PlayerEvent, EventJson = JsonWrapper.SerializeObject(request) });
            ActivateQueue();
        }

        /// <summary>
        /// Writes a title-based event into PlayStream.
        /// </summary>
        public static void WriteTitleEventQueued(WriteTitleEventRequest request)
        {
            PendingEvents.Add(new BatchedEvent { EventType = PlayStreamEventType.TitleEvent, EventJson = JsonWrapper.SerializeObject(request) });
            ActivateQueue();
        }
    }
}
#endif
