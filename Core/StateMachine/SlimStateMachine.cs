using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace Core.StateMachine
{

    #region - State Definition -
    /// <summary>
    /// The actual state machine definition data structure. This could
    /// be populated explicitly or via some XML deserialization process, or
    /// done by a specialized class filling in FirstState, States, and Transitions
    /// programmatically. Regardless of how it is made, this structure provides all
    /// the information necessary to define the (simple) state machine.
    /// 
    /// States have entry and exit methods associated, but both are optional.
    /// In your state machine implementation, when a state is entered, a method called
    /// (State)StateEntry is called, and similarly (State)StateExit is called at exit.
    /// Example: Idle will call IdleStateEntry and IdleStateExit when entering and leaving.
    /// In both cases if no method with that name is found, nothing is done.
    /// 
    /// Transitions specify the callback name directly in their specification rather
    /// than try to develop some mangled name of start and destination and event. In
    /// practice that got really ugly - a developer specified string for the method name
    /// is much cleaner.
    /// 
    /// Obviously transitions must specificy start and end states that actually exist.
    /// </summary>
    public class StateMachineDefinition
    {
        public StateMachineDefinition()
        {
            States = new List<State>();
            Transitions = new List<Transition>();
            GlobalEvents = new List<AllStateEvent>();
            ShutdownEvents = new List<int>();
        }

        /// <summary>
        /// This is the initial state of the state machine.
        /// </summary>
        public string FirstState { get; set; }

        /// <summary>
        /// Returns the collection of states
        /// </summary>
        public ICollection<State> States { get; private set; }

        /// <summary>
        /// Returns the collection of transitions
        /// </summary>
        public ICollection<Transition> Transitions { get; private set; }

        /// <summary>
        /// Returns the collection of global events
        /// </summary>
        public ICollection<AllStateEvent> GlobalEvents { get; private set; }

        /// <summary>
        /// Sequence of events to prepare shutdown
        /// </summary>
        public ICollection<int> ShutdownEvents { get; private set; }
    }

    //
    // Types used by StateMachineDefinition
    //

    public delegate void EntryHandler();
    public delegate void ExitHandler();
    public delegate void TransitionHandler();

    /// <summary>
    /// Basic state definition. By itself, doesn't do anything except specify a legal
    /// start and end for transitions and the associated events.
    /// </summary>
    public class State
    {
        public State(string name)
        {
            m_name = name;
        }

        public string m_name;
    }

    /// <summary>
    /// Transitions are where most of the action occurs, these really drive how the
    /// state machine operates.
    /// </summary>
    public class Transition
    {
        /// <summary>
        /// Specify a transition.
        /// </summary>
        /// <param name="start">Starting state</param>
        /// <param name="end">Ending state</param>
        /// <param name="ev">Event that triggers the transition. May be StandardEvents.DefaultTransition
        /// if the transition actually occurs with no event trigger.</param>
        /// <param name="meth">The method to invoke when the transition occurs.</param>
        public Transition(string start, string end, int ev, string meth)
        {
            m_startState = start; m_endState = end; m_transitionEvent = ev;
            m_transitionMethod = meth;
        }

        public string m_startState;
        public string m_endState;
        public int m_transitionEvent;
        public string m_transitionMethod;
    }

    /// <summary>
    /// An AllStateEvent (not a car accident, as you might think) is a global event
    /// handler for the state machine. In other words, regardless of what state we are
    /// in, when this event comes in, we run the attached method.
    /// Intended to simplify cases where you'd otherwise add the same reflexive transition
    /// action on every state in your chart.
    /// </summary>
    public class AllStateEvent
    {
        public AllStateEvent(int ev, string meth)
        {
            m_event = ev;
            m_method = meth;
        }

        public int m_event;
        public string m_method;
    }

    /// <summary>
    /// Standard events, reserved for use by the state machine.
    /// All non-positive numbers are reserved.
    /// </summary>
    public class StandardEvents
    {
        public const int Terminate = -1;
        public const int InvalidEvent = -2;
        public const int DefaultTransition = 0;
    }
    #endregion

    /// <summary>
    /// StateRunner is a simple state machine implementation. It is an active
    /// (threaded) class, and uses a specified state definition object to
    /// handle run time operation.
    /// 
    /// Post construction, all interaction is done with events, see PushEvent.
    /// Based upon the current state, the event may cause new state transitions to
    /// occur, and the appropriate state entry and exit methods will be called
    /// as needed.
    /// 
    /// This class exists to service clients who would like a relatively simple
    /// state machine, and thus not require the fairly heavyweight version supplied
    /// in StateMachineProcessor. This version has much less overhead, but doesn't 
    /// have quite as much flexibility. In practice, I bet it does everything most
    /// people need.
    /// </summary>
    public class StateRunner : IDisposable
    {
        private Thread m_worker;
        private BlockingCollection<int> m_events;
        private Dictionary<string, InternalState> m_states;
        private InternalState m_state;
        private TraceSwitch m_debug;
        private string m_name;
        private StringEventArgs m_args;
        private bool m_shutdownMachine = false;
        private List<int> m_shutdownEvents;

        /// <summary>
        /// EventArgs to pass a simple string
        /// </summary>
		public class StringEventArgs : EventArgs
        {
            public StringEventArgs(string d)
            {
                Data = d;
            }

            public string Data { get; set; }
        }
        /// <summary>
        /// Event fired upon state changes, the EventArgs is a StingEventArgs object,
        /// with the state name as the string.
        /// </summary>
		public event EventHandler<StringEventArgs> StateChanged;

        /// <summary>
        /// Constructs a new state machine based upon the supplied state machine
        /// definition, and implementing object.
        /// </summary>
        /// <param name="def">The state machine definition, defines the events, states, and
        /// transitions used.</param>
        /// <param name="implementor">The implementation of the state machine. This is
        /// the object which receives state entry/exit calls and transition callbacks.</param>
        /// <param name="friendlyName">A developer readable string to describe this
        /// state machine. Used as a bases for the thread's name and also used as the
        /// name for the TraceSwitch.</param>
		public StateRunner(StateMachineDefinition def, object implementor, string friendlyName)
        {
            m_name = friendlyName;
            m_debug = new TraceSwitch(friendlyName, friendlyName);
            m_events = new BlockingCollection<int>();
            m_states = new Dictionary<string, InternalState>();
            m_args = new StringEventArgs(string.Empty);
            m_shutdownEvents = new List<int>();
            BuildMappings(def, implementor);
            m_state = m_states[def.FirstState];

            // Launch thread and go
            m_worker = new Thread(StateThread)
            {
                CurrentCulture = new CultureInfo("en-US"),
                Name = friendlyName + "StateRunner"
            };
            m_worker.IsBackground = true;
            m_worker.Start();
        }

        public void Dispose()
        {
            StopStateMachine();
            m_worker.Join();
            if (m_events != null) { m_events.Dispose(); }
        }

        private void StopStateMachine()
        {
            m_shutdownMachine = true;
            // Prepare state machine to shutdown with sequence
            foreach (int shutdownEvent in m_shutdownEvents)
            {
                if (m_events != null) { AddEvent(shutdownEvent); }
            }
            // Tell the thread to die, and wait for it to actually do so.
            if (m_events != null) { AddEvent(StandardEvents.Terminate); }
        }

        /// <summary>
        /// Signals an event to the state machine. This typically causes state
        /// transitions to occur, immediately, and concurrently.
        /// </summary>
        /// <param name="ev"></param>
        /// <returns>True if event added and false if not. In the false case, a shutdown is occurring</returns>
		public bool PushEvent(int ev)
        {
            bool eventAdded = true;
            if (!m_shutdownMachine)
            {
                AddEvent(ev);
            }
            else
            {
                eventAdded = false;
            }
            return eventAdded;
        }

        /// <summary>
        /// Signals an event to the state machine. This typically causes state
        /// transitions to occur.
        /// </summary>
        /// <param name="ev"></param>
        private void AddEvent(int ev)
        {
            if (m_debug.TraceVerbose)
            {
                Debug.WriteLine("Event: " + ev, m_name);
            }
            m_events.Add(ev);
        }

        /// <summary>
        /// This returns the name of the current state. Note, this is not at all
        /// safe to use as a determination of what event you wish to fire. Such
        /// patterns defeat the entire point of event based state machines.
        /// This is ONLY to simplify some debugging tasks, not normal development,
        /// </summary>
        public string CurrentState
        {
            get { return m_state.Name; }
        }

        /// <summary>
        /// This is where we do the heavy lifting to create all of our internal structures
        /// to execute the state machine. The user-friendlier state machine definition is 
        /// converted to actual delegates and the event maps are created.
        /// </summary>
        /// <param name="def">The definition</param>
        /// <param name="implementor">The implementation of the definition</param>
		private void BuildMappings(StateMachineDefinition def, object implementor)
        {
            Type entryType = typeof(EntryHandler);
            Type exitType = typeof(ExitHandler);
            Type implementorType = implementor.GetType();
            MethodInfo[] methods = implementorType.GetMethods();

            //
            // Build States
            foreach (State stateDef in def.States)
            {
                InternalState state = new InternalState { Name = stateDef.m_name };

                // Find and bind entry/exit handlers if present.
                string entryName = stateDef.m_name + "StateEntry";
                string exitName = stateDef.m_name + "StateExit";
                foreach (MethodInfo info in methods)
                {
                    if (info.Name == entryName)
                    {
                        state.Entry = (EntryHandler)Delegate.CreateDelegate(entryType, implementor, info, true);
                    }
                    if (info.Name == exitName)
                    {
                        state.Exit = (ExitHandler)Delegate.CreateDelegate(exitType, implementor, info, true);
                    }
                }
                m_states.Add(state.Name, state);
            }

            // Build Transitions
            foreach (Transition transDef in def.Transitions)
            {
                InternalState start = m_states[transDef.m_startState];
                InternalState end = m_states[transDef.m_endState];
                InternalTransition trans = new InternalTransition();
                if (transDef.m_transitionMethod != null)
                    trans.Handler = (TransitionHandler)Delegate.CreateDelegate(typeof(TransitionHandler), implementor, transDef.m_transitionMethod);
                trans.Destination = end;
                int index = transDef.m_transitionEvent;
                AddTransition(start, index, trans);
            }

            // Build global event handlers
            // We implement these as transitions which start and end on each state, calling the method indicated.
            foreach (AllStateEvent eventDef in def.GlobalEvents)
            {
                // Build a handler
                TransitionHandler handler = (TransitionHandler)Delegate.CreateDelegate(typeof(TransitionHandler), implementor, eventDef.m_method);
                int eventNum = eventDef.m_event;
                // Add it to all states, with a destination being the same as the starting state (go nowhere).
                foreach (InternalState state in m_states.Values)
                {
                    // The one exception is if we already have a handler for this event specified in the state. In that case, it overrides
                    // the global setting for this state.
                    if ((state.Transitions.Count > eventNum) && (state.Transitions[eventNum] != null))
                        continue;
                    // Otherwise, add our global transition to this state
                    InternalTransition trans = new InternalTransition { Handler = handler, Destination = state };
                    AddTransition(state, eventNum, trans);
                }
            }

            // Build List of Shutdown Events (from already existing events)
            foreach (int shutdownEvent in def.ShutdownEvents)
            {
                m_shutdownEvents.Add(shutdownEvent);
            }
        }

        private static void AddTransition(InternalState state, int eventVal, InternalTransition trans)
        {
            // Make sure we have enough entries
            while (state.Transitions.Count <= eventVal)
            {
                state.Transitions.Add(null);
            }
            // Add our new handler
            state.Transitions[eventVal] = trans;
        }

        /// <summary>
        /// This is the main execution thread for the state machine. It processes
        /// events, does state transitions and callouts, and will shut down if it ever sees
        /// the StandardEvents.Terminate event.
        /// </summary>
        private void StateThread()
        {
            //IDiagRuntimeData diagStats = InstrumentServiceProvider.GetService<IDiagRuntimeData>();
            //if (diagStats != null)
            //{
            //    diagStats.AddCodeMarkerIf(TraceLevel.Verbose, DiagRuntimeCategory.Thread, "StateRunner.StateThread() '" + m_name + "' starting", "thread lives while state machine is active");
            //}

            int ev;
            do
            {
                // Get an event, and block if there is none.
                ev = m_events.Take();
                if (ev == StandardEvents.Terminate)
                {
                    break;
                }
                do
                {
                    IList<InternalTransition> currentTrans = m_state.Transitions;
                    InternalTransition trans = (ev < currentTrans.Count) ? currentTrans[ev] : null;
                    if (trans != null)
                    {
                        // We have a handler for this event, do the normal
                        // stuff.
                        ev = StandardEvents.InvalidEvent;
                        RunTransition(trans);
                    }
                    else
                    {
                        // Didn't have a handler for this event. Look to see if there is a
                        // default transition.
                        trans = currentTrans[StandardEvents.DefaultTransition];
                        if (trans != null)
                        {
                            // Have a default transition. Run it, leave the
                            // event queue alone.
                            RunTransition(trans);
                        }
                        else
                        {
                            if (m_debug.TraceVerbose)
                            {
                                Debug.WriteLine("No handler for " + ev + ", discarding", m_name);
                            }
                            ev = StandardEvents.InvalidEvent;
                        }
                    }
                    // No more events, but we may need to still run default transitions until we get
                    // to something that requires an event.
                    InternalTransition t;
                    while ((t = m_state.Transitions[StandardEvents.DefaultTransition]) != null)
                    {
                        // This will change m_state, of course.
                        RunTransition(t);
                    }
                } while (ev != StandardEvents.InvalidEvent);
            } while (ev != StandardEvents.Terminate);

            //if (diagStats != null)
            //{
            //    diagStats.AddCodeMarkerIf(TraceLevel.Info, DiagRuntimeCategory.Thread, "StateRunner.StateThread() '" + m_name + "' exiting");
            //}
        }

        /// <summary>
        /// Helper method to run a transition. The activities here are:
        /// 1) run exit handler for current state
        /// 2) Run any specified transition handler
        /// 3) Signal to the StateChanged event
        /// 4) Run the entry handler of the destination state
        /// 
        /// </summary>
        /// <param name="trans">Transition to run</param>
		private void RunTransition(InternalTransition trans)
        {
            if (m_debug.TraceVerbose)
            {
                Debug.WriteLine("Running transition to " + trans.Destination.Name, m_name);
            }
            if (m_state.Exit != null)
            {
                if (m_debug.TraceVerbose)
                {
                    Debug.WriteLine("Running exit handler for " + m_state.Name, m_name);
                }
                m_state.Exit();
            }
            trans.Handler?.Invoke();
            m_state = trans.Destination;
            if (m_debug.TraceInfo)
            {
                Debug.WriteLine("STATE = " + m_state.Name, m_name);
            }
            if (StateChanged != null)
            {
                m_args.Data = m_state.Name;
                StateChanged(this, m_args);
            }

            if (m_state.Entry != null)
            {
                if (m_debug.TraceVerbose)
                {
                    Debug.WriteLine("Running entry handler for " + m_state.Name, m_name);
                }
                m_state.Entry();
            }
        }

        /// <summary>
        /// Internal specification of a state, with a destination-transition map
        /// and entry/exit handlers.
        /// </summary>
		private class InternalState
        {
            public string Name;
            public EntryHandler Entry;
            public ExitHandler Exit;
            public IList<InternalTransition> Transitions;

            public InternalState()
            {
                Transitions = new List<InternalTransition>();
                Name = null;
                Entry = null;
                Exit = null;
            }
        }

        /// <summary>
        /// Internal specification of a transition, indicating destination
        /// and the transition action to run. null is acceptable as a handler.
        /// </summary>
		private class InternalTransition
        {
            public InternalState Destination;
            public TransitionHandler Handler;
        }

        /// <summary>
        /// Diagnostic tracing switch access
        /// </summary>
		public TraceLevel TraceLevel
        {
            get { return m_debug.Level; }
            set { m_debug.Level = value; }
        }
    }
}