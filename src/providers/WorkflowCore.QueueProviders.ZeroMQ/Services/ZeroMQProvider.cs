﻿using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowCore.QueueProviders.ZeroMQ.Models;

namespace WorkflowCore.QueueProviders.ZeroMQ.Services
{
    public class ZeroMQProvider : IQueueProvider
    {
        private ConcurrentQueue<string> _localRunQueue = new ConcurrentQueue<string>();
        private ConcurrentQueue<EventPublication> _localPublishQueue = new ConcurrentQueue<EventPublication>();
        private List<Thread> _serviceThreads = new List<Thread>();        
        private PushSocket _nodeSocket;
        private List<string> _peerConnectionStrings;
        private string _localConnectionString;
        private bool _active = false;
        
        public ZeroMQProvider(int port, IEnumerable<string> peers, bool canTakeWork)
        {
            _localConnectionString = "@tcp://*:" + Convert.ToString(port);
            _peerConnectionStrings = new List<string>();

            if (canTakeWork)
            {
                _peerConnectionStrings.Add(">tcp://localhost:" + Convert.ToString(port));
                foreach (var peer in peers)
                    _peerConnectionStrings.Add(">tcp://" + peer);
            }
        }

        public async Task<string> DequeueForProcessing()
        {
            string id;
            if (_localRunQueue.TryDequeue(out id))
            {
                return id;
            }
            return null;
        }

        public async Task<EventPublication> DequeueForPublishing()
        {            
            EventPublication item;
            if (_localPublishQueue.TryDequeue(out item))
            {
                return item;
            }
            return null;
        }
        
        public async Task QueueForProcessing(string Id)
        {
            PushMessage(Message.FromWorkflowId(Id));
        }

        public async Task QueueForPublishing(EventPublication item)
        {
            PushMessage(Message.FromPublication(item));
        }

        public void Start()
        {
            _nodeSocket = new PushSocket(_localConnectionString);
            _active = true;
            foreach (var connStr in _peerConnectionStrings)
            {
                PullSocket peer = new PullSocket(connStr);
                Thread thread = new Thread(new ParameterizedThreadStart(ServicePeerNode));
                thread.Start(peer);
                _serviceThreads.Add(thread);
            }   
        }

        public void Stop()
        {
            _active = false;

            foreach (var thread in _serviceThreads)
                thread.Join();

            //foreach (var peer in _peerSockets)
            //    peer.Close();

            _serviceThreads.Clear();
            _nodeSocket.Close();            
        }

        private void ServicePeerNode(object socketObj)
        {
            PullSocket socket = (socketObj as PullSocket);
            while (_active)
            {
                string data;
                if (socket.TryReceiveFrameString(TimeSpan.FromSeconds(3), out data))
                {
                    var msg = JsonConvert.DeserializeObject<Message>(data);
                    switch (msg.MessageType)
                    {
                        case MessageType.Workflow:
                            _localRunQueue.Enqueue(msg.Content);
                            break;
                        case MessageType.Publication:
                            _localPublishQueue.Enqueue(msg.ToEventPublication());
                            break;
                    }
                }
            }
            socket.Close();
        }

        private void PushMessage(Message message)
        {
            if (!_active)
                throw new Exception("ZeroMQ provider not started");

            var str = JsonConvert.SerializeObject(message);
            if (!_nodeSocket.TrySendFrame(TimeSpan.FromSeconds(3), str))
                throw new Exception("Unable to send message");
        }
        
        public void Dispose()
        {
            if (_active)
                Stop();
        }
        
    }
}
