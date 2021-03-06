/*
    Copyright (c) 2010-2011 250bpm s.r.o.
    Copyright (c) 2010-2011 Other contributors as noted in the AUTHORS file

    This file is part of 0MQ.

    0MQ is free software; you can redistribute it and/or modify it under
    the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation; either version 3 of the License, or
    (at your option) any later version.

    0MQ is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JetBrains.Annotations;

namespace NetMQ.zmq
{
    /// <summary>
    /// Base class for objects forming a part of ownership hierarchy.
    /// It handles initialisation and destruction of such objects.
    /// </summary>
    internal abstract class Own : ZObject
    {
        [NotNull]
        protected readonly Options m_options;

        /// <summary>
        /// True if termination was already initiated. If so, we can destroy
        /// the object if there are no more child objects or pending term acks.
        /// </summary>
        private bool m_terminating;

        /// <summary>
        /// Sequence number of the last command sent to this object.
        /// </summary>
        private long m_sentSeqnum;

        /// <summary>
        /// Sequence number of the last command processed by this object.
        /// </summary>
        private long m_processedSeqnum;

        /// <summary>
        /// Socket owning this object. It's responsible for shutting down
        /// this object.
        /// </summary>
        [CanBeNull]
        private Own m_owner;

        /// <summary>
        /// List of all objects owned by this socket. We are responsible
        /// for deallocating them before we quit.
        /// </summary>
        private readonly HashSet<Own> m_owned = new HashSet<Own>();

        /// <summary>
        /// Number of events we have to get before we can destroy the object.
        /// </summary>
        private int m_termAcks;

        /// <summary>
        /// Initializes a new instance of the <see cref="Own" /> class that is running on a thread outside of 0MQ infrastructure.
        /// </summary>
        /// <param name="parent">The parent context.</param>
        /// <param name="threadId">The thread id.</param>
        /// <remarks>
        /// Note that the owner is unspecified in the constructor. It'll be assigned later on using <see cref="SetOwner"/>
        /// when the object is plugged in.
        /// </remarks>
        protected Own([NotNull] Ctx parent, int threadId)
            : base(parent, threadId)
        {
            m_options = new Options();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Own" /> class that is running within I/O thread.
        /// </summary>
        /// <param name="ioThread">The I/O thread.</param>
        /// <param name="options">The options.</param>
        /// <remarks>
        /// Note that the owner is unspecified in the constructor. It'll be assigned later on using <see cref="SetOwner"/>
        /// when the object is plugged in.
        /// </remarks>
        protected Own([NotNull] IOThread ioThread, [NotNull] Options options)
            : base(ioThread)
        {
            m_options = options;
        }

        public abstract void Destroy();

        /// <summary>
        /// A place to hook in when physical destruction of the object is to be delayed.
        /// </summary>
        protected virtual void ProcessDestroy()
        {
            Destroy();
        }

        private void SetOwner([NotNull] Own owner)
        {
            Debug.Assert(m_owner == null);
            m_owner = owner;
        }

        /// <summary>
        /// When another owned object wants to send command to this object it calls this function
        /// to let it know it should not shut down before the command is delivered.
        /// </summary>
        /// <remarks>
        /// This function may be called from a different thread!
        /// </remarks>
        public void IncSeqnum()
        {
            Interlocked.Increment(ref m_sentSeqnum);
        }

        protected override void ProcessSeqnum()
        {
            //  Catch up with counter of processed commands.
            m_processedSeqnum++;

            //  We may have caught up and still have pending terms acks.
            CheckTermAcks();
        }

        /// <summary>
        /// Launch the supplied object and become its owner.
        /// </summary>
        /// <param name="obj">The object to be launched.</param>
        protected void LaunchChild([NotNull] Own obj)
        {
            //  Specify the owner of the object.
            obj.SetOwner(this);

            //  Plug the object into the I/O thread.
            SendPlug(obj);

            //  Take ownership of the object.
            SendOwn(this, obj);
        }

        /// <summary>
        /// Terminate owned object.
        /// </summary>
        /// <param name="obj"></param>
        protected void TermChild([NotNull] Own obj)
        {
            ProcessTermReq(obj);
        }

        protected override void ProcessTermReq(Own obj)
        {
            //  When shutting down we can ignore termination requests from owned
            //  objects. The termination request was already sent to the object.
            if (m_terminating)
                return;

            //  If I/O object is well and alive let's ask it to terminate.

            //  If not found, we assume that termination request was already sent to
            //  the object so we can safely ignore the request.
            if (!m_owned.Contains(obj))
                return;

            m_owned.Remove(obj);
            RegisterTermAcks(1);

            //  Note that this object is the root of the (partial shutdown) thus, its
            //  value of linger is used, rather than the value stored by the children.
            SendTerm(obj, m_options.Linger);
        }


        protected override void ProcessOwn(Own obj)
        {
            //  If the object is already being shut down, new owned objects are
            //  immediately asked to terminate. Note that linger is set to zero.
            if (m_terminating)
            {
                RegisterTermAcks(1);
                SendTerm(obj, 0);
                return;
            }

            //  Store the reference to the owned object.
            m_owned.Add(obj);
        }

        /// <summary>
        /// Ask owner object to terminate this object. It may take a while actual termination is started.
        /// </summary>
        /// <remarks>
        /// This function should not be called more than once.
        /// </remarks>
        protected void Terminate()
        {
            //  If termination is already underway, there's no point
            //  in starting it anew.
            if (m_terminating)
                return;

            if (m_owner == null)
            {
                // We are the root of the ownership tree.
                // Terminate self directly.
                ProcessTerm(m_options.Linger);
            }
            else
            {
                // When we have an owner, request them to terminate this object.
                SendTermReq(m_owner, this);
            }
        }

        /// <summary>
        /// Returns true if the object is in process of termination.
        /// </summary>
        protected bool IsTerminating { get { return m_terminating; } }

        /// <summary>
        /// Runs the termination process.
        /// </summary>
        /// <param name="linger">the linger time, in milliseconds</param>
        /// <remarks>
        /// Termination handler is protected rather than private so that it can be intercepted by the derived class.
        /// This is useful to add custom steps to the beginning of the termination process.
        /// </remarks>
        protected override void ProcessTerm(int linger)
        {
            //  Double termination should never happen.
            Debug.Assert(!m_terminating);

            //  Send termination request to all owned objects.
            foreach (Own it in m_owned)
            {
                SendTerm(it, linger);
            }

            RegisterTermAcks(m_owned.Count);
            m_owned.Clear();

            //  Start termination process and check whether by chance we cannot
            //  terminate immediately.
            m_terminating = true;
            CheckTermAcks();
        }

        /// <summary>
        /// Use following two functions to wait for arbitrary events before
        /// terminating. Just add number of events to wait for using
        /// register_tem_acks functions. When event occurs, call
        /// remove_term_ack. When number of pending acks reaches zero
        /// object will be deallocated.
        /// </summary>
        public void RegisterTermAcks(int count)
        {
            m_termAcks += count;
        }

        public void UnregisterTermAck()
        {
            Debug.Assert(m_termAcks > 0);
            m_termAcks--;

            //  This may be a last ack we are waiting for before termination...
            CheckTermAcks();
        }

        protected override void ProcessTermAck()
        {
            UnregisterTermAck();
        }

        private void CheckTermAcks()
        {
            if (m_terminating &&
                m_processedSeqnum == Interlocked.Read(ref m_sentSeqnum) &&
                m_termAcks == 0)
            {
                //  Sanity check. There should be no active children at this point.
                Debug.Assert(m_owned.Count == 0);

                //  The root object has nobody to confirm the termination to.
                //  Other nodes will confirm the termination to the owner.
                if (m_owner != null)
                    SendTermAck(m_owner);

                //  Deallocate the resources.
                ProcessDestroy();
            }
        }
    }
}
