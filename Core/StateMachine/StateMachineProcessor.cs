using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.StateMachine

{
    /// <summary>
    /// StateMachineProcessor encapsulates a thread which can be used for running FSMs 
    /// and dispatching events and timer events to them
    /// </summary>
    public class StateMachineProcessor : IDisposable
    {
        public StateMachineProcessor()
            : this("")
        {
        }

        public StateMachineProcessor(string smProcessorName)
        {
            lock (this)
            {
                // Setup debug tracing switch. 
                m_debug = new TraceSwitch("SMProc", "State Machine Processor");

                // Uncomment this line to force max debug output, regardless of switch.
                // m_debug.Level = TraceLevel.Verbose;


                m_timerListChanged = false;
                m_smProcessorName = smProcessorName;
                m_smThread = new Thread(ThreadProc)
                {
                    CurrentCulture = new System.Globalization.CultureInfo("en-US")
                };
                m_smThread.Start();

                // synchronize to execution of thread proc
                Monitor.Pulse(this);
                Monitor.Wait(this);
            }
        }

        public string Name
        {
            get { return m_smProcessorName; }
        }

        public Thread Thread
        {
            get { return m_smThread; }
        }

        /// <summary>
        /// Push a StateMachine to the processor. The processor will put it in its list
        /// and call OnSMEntry of the StateMachine in the context of its thread.  
        /// </summary>
        public void PushSM(StateMachine sm)
        {
            sm.StateMachineProcessor = this;
            lock (this)
            {
                m_jobQueue.Enqueue(sm);
                Monitor.Pulse(this);
            }
        }

        // Do NOT call directly! Used internally to remove an StateMachine
        public void PopSM(StateMachine fsm)
        {
            // Call PopSM only from processor thread.
            Debug.Assert(Thread.CurrentThread.GetHashCode() == m_smThread.GetHashCode());
            m_smList.Remove(fsm.GetHashCode());
            // remove timers for StateMachine
            // runs on StateMachine processor thread
            for (int i = 0; i < m_timerList.Count; i++)
            {
                if (((StateMachineTimerEvent)m_timerList[i]).StateMachine.GetHashCode() == fsm.GetHashCode())
                {
                    ((StateMachineTimerEvent)m_timerList[i]).Enable = false;
                }
            }
        }


        // Terminate a specific StateMachine. this call is asynchronous. To wait on completion,
        // call the FSMs "Wait" method.
        public void TerminateSM(StateMachine fsm)
        {
            if (Thread.CurrentThread.GetHashCode() == m_smThread.GetHashCode())
            {
                RemoveSM(fsm);
            }
            else
            {
                TerminateSMEvent ev = new TerminateSMEvent();
                PushEvent(ev, fsm);
            }
        }

        public void TerminateAllSM()
        {
            if (Thread.CurrentThread.GetHashCode() == m_smThread.GetHashCode())
            {
                RemoveAllSMs();
            }
            else
            {
                TerminateSMEvent ev = new TerminateSMEvent();
                PushEvent(ev, null);
            }
        }

        private StateMachineEvent _lastQueued = null;
        public void PushEvent(StateMachineEvent smEvent, StateMachine sm)
        {
            lock (m_jobQueue.SyncRoot)
            {
                if (m_debug.TraceVerbose)
                {
                    Trace.WriteLine("PushEvent " + DateTime.Now.TimeOfDay + " " + smEvent.ToString(), m_name);
                }

                if (_lastQueued != smEvent || m_jobQueue.Count == 0)
                {
                    smEvent.StateMachine = sm;
                    _lastQueued = smEvent;
                    m_jobQueue.Enqueue(smEvent);
                    Monitor.Pulse(m_jobQueue.SyncRoot);
                }
            }
        }

        public void PushEvent(StateMachineTimerEvent smTimerEvent, StateMachine sm)
        {
            lock (m_jobQueue.SyncRoot)
            {
                smTimerEvent.StateMachine = sm;
                bool inserted = false;
                for (int i = 0; i < m_timerList.Count; i++)
                {
                    if (((StateMachineTimerEvent)m_timerList[i]).ExpiryTime >= smTimerEvent.ExpiryTime)
                    {
                        m_timerList.Insert(i, smTimerEvent);
                        inserted = true;
                        break;
                    }
                }
                if (!inserted)
                {
                    m_timerList.Add(smTimerEvent);
                }
                m_timerListChanged = true;
                Monitor.Pulse(m_jobQueue.SyncRoot);
            }
        }

        public void RemoveTimerEvent(StateMachineTimerEvent fsmTimerEvent)
        {
            lock (this)
            {
                m_timerList.Remove(fsmTimerEvent);
                m_timerList.Insert(0, fsmTimerEvent);
                m_timerListChanged = true;
                Monitor.Pulse(this);
            }
        }

        public bool Contains(StateMachineEvent smEvent)
        {
            lock (m_jobQueue.SyncRoot)
            {
                return m_jobQueue.Contains(smEvent);
            }
        }

        private void CheckTimerEvents()
        {
            m_timerListChanged = false;

            // Check, if there are some waiting timer events and treat them
            if (m_timerList.Count == 0)
            {
                m_restTimeMS = Timeout.Infinite;
                return;
            }

            // remove and skip disabled timer events
            while ((m_timerList.Count > 0) &&
                ((StateMachineTimerEvent)m_timerList[0]).Enable == false)
            {
                m_timerList.RemoveAt(0);
            }

            // Check, if there are still some waiting timer events and treat them
            if (m_timerList.Count == 0)
            {
                m_restTimeMS = Timeout.Infinite;
                return;
            }

            m_restTimeMS = 0;
            while ((m_timerList.Count > 0) && (m_restTimeMS == 0))
            {
                StateMachineTimerEvent timerFsmEvent = (StateMachineTimerEvent)m_timerList[0];
                // TimeSpan restTime = ((StateMachineTimerEvent)m_timerList[0]).ExpiryTime - DateTime.Now;
                TimeSpan restTime = timerFsmEvent.ExpiryTime.Subtract(DateTime.Now);
                m_restTimeMS = (int)restTime.TotalMilliseconds;
                if (m_restTimeMS <= 0)
                {
                    // this timer event has expired
                    m_restTimeMS = 0;
                    m_timerList.RemoveAt(0);
                    StateMachine fsm = (StateMachine)m_smList[timerFsmEvent.StateMachine.GetHashCode()];
                    if (fsm != null)
                    {
                        System.Threading.Monitor.Exit(this);
                        fsm.OnSMEvent(timerFsmEvent);
                        System.Threading.Monitor.Enter(this);
                        if (timerFsmEvent.Repeats < int.MaxValue)
                        {
                            if (timerFsmEvent.Repeats == 0)
                            {
                                timerFsmEvent.Done();
                            }
                            else
                            {
                                timerFsmEvent.DecRepeats();
                                PushEvent(timerFsmEvent, timerFsmEvent.StateMachine);
                            }
                        }
                    }
                }
            } // while

            // if there are no timer events anymore, we have to wait for next event
            if (m_timerList.Count == 0)
                m_restTimeMS = Timeout.Infinite;
        }

        private void ThreadProc()
        {
            //IDiagRuntimeData diagStats = InstrumentServiceProvider.GetService<IDiagRuntimeData>();
            //if (diagStats != null) diagStats.AddCodeMarkerIf(TraceLevel.Verbose, DiagRuntimeCategory.Thread, "StateMachineProcessor.ThreadProc() '" + m_name + "' starting", "thread lives while state machine is active");
            try
            {

                StateMachineEventBase jobObj;
                bool allDone = false;

                lock (this)
                {
                    Thread.CurrentThread.Name = m_smProcessorName;
                    Monitor.Pulse(this); // synchronize constructor
                }

                // The main "do until shutdown" loop
                while (!allDone)
                {
                    lock (m_jobQueue.SyncRoot)
                    {
                        if (m_jobQueue.Count == 0)
                        {
                            _isRunning = false;
                            Monitor.Wait(m_jobQueue.SyncRoot, -1, true);
                            _isRunning = true;
                        }

                        // Get next queued event
                        jobObj = (StateMachineEventBase)m_jobQueue.Dequeue();
                    }

                    if (typeof(StateMachine).IsInstanceOfType(jobObj))
                    {
                        m_smList.Add(jobObj.GetHashCode(), jobObj);
                        ((StateMachine)jobObj).OnSMEntry();
                        ((StateMachine)jobObj).EnterFirstState();
                    }
                    else if (typeof(TerminateSMEvent).IsInstanceOfType(jobObj))
                    {
                        TerminateSMEvent ev = (TerminateSMEvent)jobObj;
                        if (ev.StateMachine != null)
                        {
                            // terminate a single StateMachine
                            RemoveSM(ev.StateMachine);
                        }
                        else // terminate all SMs and 
                            RemoveAllSMs();
                        allDone = true;
                    }
                    else if (typeof(TerminateEvent).IsInstanceOfType(jobObj))
                    {
                        allDone = true;
                    }
                    else if (typeof(StateMachineTimerEvent).IsInstanceOfType(jobObj))
                    {
                        PushEvent((StateMachineTimerEvent)jobObj, ((StateMachineTimerEvent)jobObj).StateMachine);
                        // Trace.WriteLine("CheckTimerEvents upon event " + jobObj.ToString());
                        CheckTimerEvents();
                    }
                    else
                    { // it is another event, dispatch it to the right StateMachine
                        StateMachine sm = (StateMachine)m_smList[((StateMachineEvent)jobObj).StateMachine.GetHashCode()];
                        if (sm != null)
                        {
                            sm.OnSMEvent((StateMachineEvent)jobObj);
                            ((StateMachineEvent)jobObj).Done();
                        }
                        else
                        {
                            Trace.WriteLine("StateMachine not found for StateMachineEvent " + jobObj.ToString());
                        }
                    }
                    // process timer elapsed in the mean time
                    if (m_timerListChanged)
                    {
                        // Trace.WriteLine("CheckTimerEvents after event handling");
                        CheckTimerEvents();
                    }

                } // end while

                // remove all StateMachine's from queue
                RemoveAllSMs();

            }

            catch (System.Exception caught)
            {
                Exception exp = caught;
                if (caught.InnerException != null)
                {
                    exp = caught.InnerException;
                }


                Trace.WriteLine("Exception caught: " + exp.ToString(),
                    m_smProcessorName);

                Debug.Assert(false, "Exception Caught at State Machine thread<" + Name + ">", exp.ToString());
                //SystemInternal.MessageLogger.Instance.PublishException("StateMachineProcessor::ThreadProc", exp);

                //System.Windows.Forms.Application.Exit();
            }
            //if (diagStats != null) diagStats.AddCodeMarkerIf(TraceLevel.Info, DiagRuntimeCategory.Thread, "StateMachineProcessor.ThreadProc() '" + m_name + "' exiting");
        }

        private void RemoveSM(StateMachine sm)
        {
            PopSM(sm);
            sm.OnSMExit();
            sm.Done();
            sm.StateMachineProcessor = null;
        }

        private void RemoveAllSMs()
        {
            // remove all StateMachine's from queue
            Hashtable smsHT = (Hashtable)m_smList.Clone();
            foreach (StateMachine sm in smsHT.Values)
            {
                RemoveSM(sm);
            }
        }

        public void Dispose()
        {
            PushEvent(new TerminateEvent(), null);
        }


        /// <summary>
        /// Suspend current running thread
        /// </summary>
        public void Suspend()
        {
            lock (m_smThread)
            {
                if (_isPauseRequested)
                {
                    _pausing = true;
                    Monitor.Wait(m_smThread);
                    _pausing = false;
                }
            }
        }


        /// <summary>
        /// Resume current suspending thread
        /// </summary>
        public void Resume()
        {
            lock (m_smThread)
            {
                _isPauseRequested = false;

                if (_pausing)
                    Monitor.PulseAll(m_smThread);
            }
        }

        public bool IsPauseRequested
        {
            set
            {
                lock (m_smThread)
                {
                    _isPauseRequested = value;
                }
            }
            get { return _isPauseRequested; }
        }

        // Debug assistance
        private TraceSwitch m_debug;
        private string m_name = "SMProc";

        private Thread m_smThread = null;
        private bool _pausing;
        private bool _isPauseRequested;

        private Hashtable m_smList = new Hashtable();
        private Queue m_jobQueue = new Queue();
        private ArrayList m_timerList = new ArrayList();
        private int m_restTimeMS;
        string m_smProcessorName;
        bool m_timerListChanged;

        public Queue JobQueue
        {
            get { return m_jobQueue; }
        }

        private volatile bool _isRunning;
        public bool IsRunning
        {
            get { return _isRunning; }
        }

    }
}
