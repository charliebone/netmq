﻿using System;
using System.Diagnostics;
using System.Threading;
using JetBrains.Annotations;
using NetMQ.Sockets;
using NetMQ.zmq;

namespace NetMQ.SimpleTests
{
    internal abstract class ThroughputBenchmarkBase : ITest
    {
        private static readonly int[] s_messageSizes = { 8, 64, 256, 1024, 4096 };

        protected const int MsgCount = 1000000;

        public string TestName { get; protected set; }

        public void RunTest()
        {
            Console.Out.WriteLine(" Messages: {0:#,##0}", MsgCount);
            Console.Out.WriteLine();
            Console.Out.WriteLine(" {0,-6} {1,10} {2,8}", "Size", "Msgs/sec", "Mb/s");
            Console.Out.WriteLine("----------------------------");

            var consumer = new Thread(ConsumerThread) { Name = "Consumer" };
            var producer = new Thread(ProducerThread) { Name = "Producer" };

            consumer.Start();
            producer.Start();

            producer.Join();
            consumer.Join();
        }

        private void ConsumerThread()
        {
            using (var context = NetMQContext.Create())
            using (var socket = CreateConsumerSocket(context))
            {
                socket.Bind("tcp://*:9091");

                foreach (var messageSize in s_messageSizes)
                {
                    var watch = Stopwatch.StartNew();

                    Consume(socket, messageSize);

                    long ticks = watch.ElapsedTicks;
                    double seconds = (double)ticks/Stopwatch.Frequency;
                    double msgsPerSec = MsgCount/seconds;
                    double megabitsPerSec = msgsPerSec*messageSize*8/1000000;

                    Console.Out.WriteLine(" {0,-6} {1,10:0.0} {2,8:0.00}", messageSize, msgsPerSec, megabitsPerSec);
                }
            }
        }

        private void ProducerThread()
        {
            using (var context = NetMQContext.Create())
            using (var socket = CreateProducerSocket(context))
            {
                socket.Connect("tcp://127.0.0.1:9091");

                foreach (var messageSize in s_messageSizes)
                    Produce(socket, messageSize);
            }
        }

        [NotNull] protected abstract PushSocket CreateProducerSocket([NotNull] NetMQContext context);
        [NotNull] protected abstract PullSocket CreateConsumerSocket([NotNull] NetMQContext context);

        protected abstract void Produce([NotNull] PushSocket socket, int messageSize);
        protected abstract void Consume([NotNull] PullSocket socket, int messageSize);
    }

    internal class ThroughputBenchmark : ThroughputBenchmarkBase
    {
        public ThroughputBenchmark()
        {
            TestName = "Push/Pull Throughput Benchmark";
        }

        protected override PushSocket CreateProducerSocket(NetMQContext context)
        {
            return context.CreatePushSocket();
        }

        protected override PullSocket CreateConsumerSocket(NetMQContext context)
        {
            return context.CreatePullSocket();
        }

        protected override void Produce(PushSocket socket, int messageSize)
        {
            var msg = new byte[messageSize];
            msg[messageSize/2] = 0x42;

            for (int i = 0; i < MsgCount; i++)
                socket.Send(msg);
        }

        protected override void Consume(PullSocket socket, int messageSize)
        {
            for (int i = 0; i < MsgCount; i++)
            {
                var message = socket.Receive();
                Debug.Assert(message.Length == messageSize, "Message length was different from expected size.");
                Debug.Assert(message[messageSize/2] == 0x42, "Message did not contain verification data.");
            }
        }
    }

    internal class ThroughputBenchmarkReusingMsg : ThroughputBenchmarkBase
    {
        public ThroughputBenchmarkReusingMsg()
        {
            TestName = "Push/Pull Throughput Benchmark (reusing Msg)";
        }

        protected override PushSocket CreateProducerSocket(NetMQContext context)
        {
            return context.CreatePushSocket();
        }

        protected override PullSocket CreateConsumerSocket(NetMQContext context)
        {
            return context.CreatePullSocket();
        }

        protected override void Produce(PushSocket socket, int messageSize)
        {
            var msg = new Msg();
            msg.InitGC(new byte[messageSize], messageSize);
            msg.Data[messageSize/2] = 0x42;

            for (int i = 0; i < MsgCount; i++)
                socket.Send(ref msg, SendReceiveOptions.None);
        }

        protected override void Consume(PullSocket socket, int messageSize)
        {
            var msg = new Msg();
            msg.InitEmpty();

            for (int i = 0; i < MsgCount; i++)
            {
                socket.Receive(ref msg, SendReceiveOptions.None);
                Debug.Assert(msg.Data.Length == messageSize, "Message length was different from expected size.");
                Debug.Assert(msg.Data[msg.Size/2] == 0x42, "Message did not contain verification data.");
            }
        }
    }
}