using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.StateMachine

{
    /// <summary>
    /// Summary description for StateMachineEvent.
    /// </summary>
    public class StateMachineEventBase
    {
        private Object _synchRoot;
        private Object _innerSynch = new Object();
        private bool m_isDone = false;

        public StateMachineEventBase()
        {
            _synchRoot = new Object();
        }

        public StateMachineEventBase(object synch)
        {
            _synchRoot = synch;
        }

        public virtual void Done()
        {
            lock (_synchRoot)
            {
                lock (_innerSynch)
                {
                    m_isDone = true;
                    Monitor.Pulse(_innerSynch);
                }
            }
        }

        public virtual void Wait()
        {
            lock (_innerSynch)
            {
                if (!m_isDone)
                    Monitor.Wait(_innerSynch);
            }
        }

        public virtual bool IsDone
        {
            get
            {
                lock (_innerSynch)
                {
                    return m_isDone;
                }
            }
        }
    }

    /// <summary>
    /// Summary description for StateMachineEvent.
    /// </summary>
    public class StateMachineEvent : StateMachineEventBase
    {
        public StateMachineEvent()
        {
        }

        public StateMachineEvent(object synch)
            : base(synch)
        {
        }

        public StateMachine StateMachine
        {
            set { m_sm = value; }
            get { return m_sm; }
        }

        private StateMachine m_sm = null;
    }

    /// <summary>
    /// 
    /// </summary>
    public class StateMachineTimerEvent : StateMachineEvent
    {
        public StateMachineTimerEvent(DateTime expiryTime)
        {
            m_expiryTime = expiryTime;
        }
        public StateMachineTimerEvent(TimeSpan delayTime)
        {
            m_expiryTime = DateTime.Now + delayTime;
            m_nOfRepeats = 0;
        }
        /// <summary>
        /// Create a timer event, which timeout is repeated n time
        /// nOfRepeats is int.MaxValue, it repeats endless. 
        /// </summary>
        /// <param name="delayTime"></param>
        /// <param name="nOfRepeats"></param>
        public StateMachineTimerEvent(TimeSpan delayTime, int nOfRepeats)
        {
            m_expiryTime = DateTime.Now + delayTime;
            m_nOfRepeats = nOfRepeats;
        }
        public DateTime ExpiryTime
        {
            set { m_expiryTime = value; }
            get { return m_expiryTime; }
        }
        public TimeSpan ExpiryDelay
        {
            get { return m_expiryTime.Subtract(DateTime.Now); }
        }
        public bool Enable
        {
            set { m_enabled = value; }
            get { return m_enabled; }
        }
        public int Repeats
        {
            get { return m_nOfRepeats; }
        }

        public void DecRepeats()
        {
            if (m_nOfRepeats > 0)
                m_nOfRepeats--;
        }

        private DateTime m_expiryTime;
        private bool m_enabled = true;
        private int m_nOfRepeats;
    }

    /// <summary>
    /// Terminates a single StateMachine or all running FSMs
    /// </summary>
    public class TerminateSMEvent : StateMachineEvent
    {
        public TerminateSMEvent()
        {
        }
    }

    /// <summary>
    /// Terminates StateMachine processor
    /// </summary>
    public class TerminateEvent : StateMachineEvent
    {
        public TerminateEvent()
        {
        }
    }
}
