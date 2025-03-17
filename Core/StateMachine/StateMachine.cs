using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace Core.StateMachine
{
    /// <summary>
    /// StateMachine is the base class for user defined state machines.
    /// During startup time, StateMachine investigates the user defined handler methods 
    /// with reflection and creates appropriate structures for run time.
    /// At run time it dispatches the events according to the investigated info. 
    /// </summary>
    public abstract class StateMachine : StateMachineEventBase
    {
        private static bool HasFieldAttributeOfType(
            MemberInfo m,
            object filterCriteria)
        {
            return m.GetCustomAttributes((Type)filterCriteria, false).Length > 0;
        }

        private static bool IsValidSMHandler(
            MemberInfo m,
            object filterCriteria)
        {
            Debug.Assert(m.MemberType == MemberTypes.Method);
            MethodInfo mi = (MethodInfo)m;

            // handler methods are never static
            if (mi.IsStatic)
                return false;

            // discriminate methods with "FsmNoHandler" attribute
            if (mi.GetCustomAttributes(typeof(SMNoHandlerAttribute), false).Length > 0)
                return false;

            // check parameter(s)

            // Method must not have a return type
            if (mi.ReturnType != typeof(void))
                return false;

            // Method must have max. two parameters 
            ParameterInfo[] pi = mi.GetParameters();
            if (pi.Length > 2)
                return false;

            // Method must not be defined in the StateMachine base type
            if (mi.DeclaringType == typeof(StateMachine))
                return false;

            // else succeed
            return true;
        }

        // can not be done in static constructor since derived class must already exist
        void InitializeSMClassInfo()
        {
            Type typeSM = GetType();
            Debug.Assert(m_classInfo == null);
            m_classInfo = new SMClassInfo();
            m_classInfo.m_Name = typeSM.Name;
            SMClassInfo.m_smClassInfoMap[typeSM] = m_classInfo;

            // Check, if client want to use attributes for defining state var and handler
            Object[] smCodingAttributes = typeSM.GetCustomAttributes(typeof(SMCodingAttribute), true);
            Debug.Assert(smCodingAttributes.Length <= 1);
            if (smCodingAttributes.Length > 0)
            {
                SMCodingAttribute attr = (SMCodingAttribute)smCodingAttributes[0];
                m_classInfo.m_smCodingType = attr.CodingType;
            }

            // get state member variable if available
            MemberInfo[] stateFields;
            if (m_classInfo.m_smCodingType == ECodingType.Automatic)
            {
                // No attribute needed, just take the member with the name "m_State"
                stateFields = typeSM.GetMember(
                    "m_State",
                    MemberTypes.Field,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            else
            {
                // Find a enum member with the attribute "FsmState"
                stateFields = typeSM.FindMembers(MemberTypes.Field,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    new MemberFilter(HasFieldAttributeOfType), typeof(SMStateAttribute));

                // Generate a GetCurrentState method for this StateMachine class
                // Note: don't know how to implement ....
            }

            // Get all info from state valiable and prepare state info
            FieldInfo stateField;
            FieldInfo[] StateEnums;
            Hashtable stringToStateMap = new Hashtable();
            if (stateFields.Length > 0)
            {
                m_classInfo.m_hasStates = true;
                // Get transition handlers and state handlers
                // This StateMachine has a state field, get the filed info of it.
                // Fill a string map with the enumeration value names for faster lookup
                Debug.Assert(stateFields.Length == 1);
                stateField = (FieldInfo)stateFields[0];
                m_classInfo.m_stateEnumType = stateField.FieldType;
                Debug.Assert(m_classInfo.m_stateEnumType.IsSubclassOf(typeof(System.Enum)));
                StateEnums = m_classInfo.m_stateEnumType.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                m_classInfo.m_StateInfoArray = new StateInfo[StateEnums.Length];

                foreach (FieldInfo fi in StateEnums)
                {
                    int val = (int)fi.GetValue(null);
                    StateInfo stateInfo = new StateInfo();
                    m_classInfo.m_StateInfoArray[val] = stateInfo;
                    Debug.Assert(m_classInfo.m_StateInfoArray[val] == stateInfo);
                    stateInfo.m_name = fi.Name;
                    stateInfo.m_transitions = new Hashtable();
                    stringToStateMap.Add(fi.Name, stateInfo);
                }
            }

            // Get all methods which are candidates for any kind of handlers
            MemberInfo[] handlers = typeSM.FindMembers(MemberTypes.Method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                new MemberFilter(IsValidSMHandler), null);

            // Loop over all these methods
            foreach (MemberInfo mi in handlers)
            {
                MethodInfo meth = (MethodInfo)mi;
                StateInfo stateInfo = null;
                if (m_classInfo.m_smCodingType == ECodingType.Automatic)
                {
                    // check if it is a state or transition handler
                    bool bIsStateOrTransitionHandler = false;
                    int separatorPos = meth.Name.IndexOf("_");
                    if (separatorPos >= 0)
                    {
                        string prefix = meth.Name.Substring(0, separatorPos);
                        stateInfo = (StateInfo)stringToStateMap[prefix];
                        if (stateInfo != null)
                        {
                            // It is a state or transition handler
                            bIsStateOrTransitionHandler = true;
                        }
                    }

                    if (bIsStateOrTransitionHandler)
                    {
                        if (meth.Name.EndsWith("_EntryState"))
                        {
                            Debug.Assert(meth.GetParameters().Length == 2);
                            Debug.Assert(meth.GetParameters()[0].ParameterType == typeof(StateMachineEvent));
                            Debug.Assert(meth.GetParameters()[1].ParameterType == m_classInfo.m_stateEnumType);
                            stateInfo.m_entryMethod = meth;
                        }
                        else if (meth.Name.EndsWith("_ExitState"))
                        {
                            Debug.Assert(meth.GetParameters().Length == 1);
                            Debug.Assert(meth.GetParameters()[0].ParameterType == typeof(StateMachineEvent));
                            stateInfo.m_exitMethod = meth;
                        }
                        else if (meth.GetParameters().Length == 1)
                        {
                            if (meth.GetParameters()[0].ParameterType == typeof(StateMachineEvent))
                            {
                                // it is a default transition
                                stateInfo.m_defaultTransitionMethod = meth;
                            }
                            else if (meth.GetParameters()[0].ParameterType.IsSubclassOf(typeof(StateMachineEvent)))
                            {
                                // it is a transition
                                Type eventParamType = meth.GetParameters()[0].ParameterType;
                                Debug.Assert(stateInfo.m_transitions[eventParamType] == null);
                                stateInfo.m_transitions[eventParamType] = meth;
                            }
                            else
                            {
                                // Do nothing, it is not a StateMachine method
                            }
                        }
                        else
                        {
                            // Do nothing, it is not a StateMachine method
                        }
                    }
                    else
                    {
                        if (meth.GetParameters().Length == 1 &&
                            meth.GetParameters()[0].ParameterType.IsSubclassOf(typeof(StateMachineEvent)))
                        {
                            // Its an event handler
                            Type eventParamType = meth.GetParameters()[0].ParameterType;
                            // add [FsmNoHandler] to failing method when asserting here!
                            Debug.Assert(m_classInfo.m_EventHandlers[eventParamType] == null);
                            m_classInfo.m_EventHandlers[eventParamType] = meth;
                        }
                        else
                        {
                            // Do nothing, it is not a StateMachine method
                        }
                    }
                }
                else
                {
                    // NOT Automatic, with attributes
                    object[] attribs;

                    // Is it a transition handler ?
                    attribs = mi.GetCustomAttributes(typeof(SMTransitionHandlerAttribute), false);
                    if (attribs.Length > 0)
                    {
                        // yes, it is a transition handler, assign it to state info
                        SMTransitionHandlerAttribute attrib = (SMTransitionHandlerAttribute)attribs[0];
                        stateInfo = (StateInfo)stringToStateMap[attrib.FromState];
                        Debug.Assert(stateInfo != null);
                        Type eventParamType = meth.GetParameters()[0].ParameterType;

                        // Is it the default transition handler ?
                        if (eventParamType == typeof(StateMachineEvent))
                        {
                            // Yes, store it
                            stateInfo.m_defaultTransitionMethod = meth;
                        }
                        else
                        {
                            // It is a normal transiton handler
                            Debug.Assert(eventParamType.IsSubclassOf(typeof(StateMachineEvent)));
                            Debug.Assert(stateInfo.m_transitions[eventParamType] == null);
                            stateInfo.m_transitions[eventParamType] = meth;
                        }
                    }
                    else
                    {
                        attribs = mi.GetCustomAttributes(typeof(SMStateHandlerAttribute), false);
                        if (attribs.Length > 0)
                        {
                            // yes, it is a state handler
                            SMStateHandlerAttribute attrib = (SMStateHandlerAttribute)attribs[0];
                            stateInfo = (StateInfo)stringToStateMap[attrib.State];
                            Debug.Assert(stateInfo != null);
                            if (attrib.HandlerType == EStateHandlerType.Entry)
                            {
                                Debug.Assert(meth.GetParameters().Length == 2);
                                Debug.Assert(meth.GetParameters()[0].ParameterType == typeof(StateMachineEvent));
                                Debug.Assert(meth.GetParameters()[1].ParameterType == m_classInfo.m_stateEnumType);
                                Debug.Assert(stateInfo.m_entryMethod == null);
                                stateInfo.m_entryMethod = meth;
                            }
                            else if (attrib.HandlerType == EStateHandlerType.Exit)
                            {
                                Debug.Assert(meth.GetParameters().Length == 1);
                                Debug.Assert(meth.GetParameters()[0].ParameterType == typeof(StateMachineEvent));
                                Debug.Assert(stateInfo.m_exitMethod == null);
                                stateInfo.m_exitMethod = meth;
                            }
                            else
                            {
                                Trace.WriteLine("Unexpected State Handler attribute value");
                                Debug.Assert(false);
                            }
                        }
                        else
                        {
                            // it is neither a transition nor a state handler
                            // Is it a event handler
                            attribs = mi.GetCustomAttributes(typeof(SMEventHandlerAttribute), false);
                            if (attribs.Length > 0)
                            {
                                // yes, it is an event handler
                                SMEventHandlerAttribute attrib = (SMEventHandlerAttribute)attribs[0];
                                Type eventParamType = meth.GetParameters()[0].ParameterType;
                                Debug.Assert(m_classInfo.m_EventHandlers[eventParamType] == null);
                                Debug.Assert(eventParamType.IsSubclassOf(typeof(StateMachineEvent)));
                                m_classInfo.m_EventHandlers[eventParamType] = meth;
                            }
                        }
                    }
                }
            }
        }

        public StateMachine()
        {
            m_classInfo = (SMClassInfo)SMClassInfo.m_smClassInfoMap[GetType()];
            if (m_classInfo == null)
            {
                InitializeSMClassInfo();
            }
            Debug.Assert(m_classInfo != null);
        }

        public StateMachineProcessor StateMachineProcessor
        {
            set { m_smProcessor = value; }
            get { return m_smProcessor; }
        }

        public bool HasStates
        {
            get { return this.m_classInfo.m_hasStates; }
        }

        public void PushEvent(StateMachineEvent ev)
        {
            if (StateMachineProcessor != null)
                StateMachineProcessor.PushEvent(ev, this);
        }

        public void PushEvent(StateMachineTimerEvent ev)
        {
            if (StateMachineProcessor != null)
                StateMachineProcessor.PushEvent(ev, this);
        }

        public void Terminate()
        {
            if (StateMachineProcessor != null)
                StateMachineProcessor.TerminateSM(this);
        }

        public virtual void OnSMEntry()
        {
            // do nothing, override this method in derived classes
        }

        public virtual void OnSMExit()
        {
            // do nothing, override this method in derived classes
        }

        public void EnterFirstState()
        {
            Debug.Assert(m_classInfo != null);
            if (m_classInfo.m_hasStates)
            {
                Type typeSM = this.GetType();
                Int32 currState = CurrentState;
                StateInfo stateInfo = m_classInfo.m_StateInfoArray[currState];
                // Execute Entry state handler of current first state
                if (stateInfo.m_entryMethod != null)
                {
                    try
                    {
                        stateInfo.m_entryMethod.Invoke(this,
                            new Object[] { null, Enum.ToObject(m_classInfo.m_stateEnumType, currState) });
                        // it is not allowed to change state in State Entry Handler!
                        Debug.Assert(currState == CurrentState);
                    }
                    catch (TargetInvocationException e)
                    {
                        //MessageLogger.Instance.PublishException("StateMachine::EnterFirstState, " + m_classInfo.m_Name + " Current State: " + currState.ToString(), e);
                    }
                }
            }
        }

        public virtual void OnSMEvent(StateMachineEvent smEvent)
        {
            try
            {

                Debug.Assert(m_classInfo != null);
                if (m_classInfo.m_hasStates)
                {
                    Int32 currState = CurrentState;
                    // execute transition
                    StateInfo stateInfo1 = m_classInfo.m_StateInfoArray[currState];
                    Debug.Assert(stateInfo1 != null);
                    MethodInfo methInfoTransition = (MethodInfo)stateInfo1.m_transitions[smEvent.GetType()];
                    if (methInfoTransition != null)
                    {
                        // first execute exit method
                        if (stateInfo1.m_exitMethod != null)
                        {
                            stateInfo1.m_exitMethod.Invoke(this, new Object[] { smEvent });
                            // it is not allowed to change state in State Exit Handler!
                            Debug.Assert(currState == CurrentState);
                        }

                        // now execute transition method
                        methInfoTransition.Invoke(this, new Object[] { smEvent });
                        // on return, StateMachine has probably a new state

                        int newState = CurrentState;
                        if (newState != currState)
                        {
                            // Transition to other state
                            StateInfo stateInfo2 = m_classInfo.m_StateInfoArray[newState];
                            Debug.Assert(stateInfo2 != null);

                            // Execute Entry state handler of new state
                            if (stateInfo2.m_entryMethod != null)
                            {
                                stateInfo2.m_entryMethod.Invoke(this, new Object[]
                                    {smEvent, Enum.ToObject(m_classInfo.m_stateEnumType,currState)});
                                // it is not allowed to change state in State Entry Handler!
                                Debug.Assert(newState == CurrentState);
                            }
                        }
                        else
                        { // It is the same state --> transition loop
                            if (stateInfo1.m_entryMethod != null)
                            {
                                stateInfo1.m_entryMethod.Invoke(this, new Object[]
                                    {smEvent, Enum.ToObject(m_classInfo.m_stateEnumType,currState)});
                                // it is not allowed to change state in State Entry Handler!
                                Debug.Assert(currState == CurrentState);
                            }
                        }
                    }
                    else
                    {
                        // There is no transition for this event in current state.
                        if (stateInfo1.m_defaultTransitionMethod != null)
                        {
                            stateInfo1.m_defaultTransitionMethod.Invoke(this, new Object[] { smEvent });
                        }
                        else
                        {
                            // Try to find an event handler
                            MethodInfo methInfoEvHnd = (MethodInfo)m_classInfo.m_EventHandlers[smEvent.GetType()];
                            if (methInfoEvHnd != null)
                            {
                                methInfoEvHnd.Invoke(this, new Object[] { smEvent });
                            }
                            else
                            {
                                // Call default handler
                                OnSMEventDefault(smEvent);
                            }
                        }
                    }
                }
                else
                {
                    // StateMachine has no states. Therefore event handlers are the only handlers to check.
                    MethodInfo mi = (MethodInfo)m_classInfo.m_EventHandlers[smEvent.GetType()];
                    if (mi != null)
                    {
                        mi.Invoke(this, new Object[] { smEvent });
                    }
                    else
                    {
                        // error: No matching event handler available
                        Trace.WriteLine("StateMachine:" + this.GetType().Name + " Event:"
                            + smEvent.GetType().Name + " no event handler found");
                    }
                }

            }

            catch (UnhandledEventException ex)
            {
                Trace.WriteLine(DateTime.Now.TimeOfDay +
                                "StateMachine.OnSMEvent() Exception caught:");
                Trace.WriteLine("    " + ex.Message);
                //MessageLogger.Instance.PublishException("StateMachine::OnSMEvent", ex);
            }

        }

        protected virtual void OnSMEventDefault(StateMachineEvent smEvent)
        {
            // error: There exists neither a transition handler nor an event handler
            if (m_classInfo.m_hasStates)
            {
                Trace.WriteLine("StateMachine:" + this.GetType().Name + " Event:" + smEvent.GetType().Name + " not handled in state:"
                        + Enum.GetName(m_classInfo.m_stateEnumType, CurrentState));
            }
            else
            {
                Trace.WriteLine("StateMachine:" + this.GetType().Name + " Event:" + smEvent.GetType().Name + " not handled");
            }
        }

        public virtual void OnTimer(StateMachineTimerEvent timerFsmEvent) { }

        public virtual void SMDone()
        {
            if (Thread.CurrentThread.GetHashCode() == m_smProcessor.Thread.GetHashCode())
            {
                m_smProcessor.PopSM(this);
                OnSMExit();
                Done();
            }
            else
            {
                m_smProcessor.TerminateSM(this);
            }
        }

        protected virtual int CurrentState
        {
            get
            {
                Debug.Assert(false);
                Trace.WriteLine("GetCurrentState must be overridden when defining a state variable");
                return -1;
            }
        }

        private StateMachineProcessor m_smProcessor = null;
        private SMClassInfo m_classInfo = null;
    } // class StateMachine

    public class SMClassInfo
    {
        public SMClassInfo()
        {
            m_EventHandlers = new Hashtable();
            m_smCodingType = ECodingType.Automatic;
            m_StateInfoArray = null;
        }
        public Hashtable m_EventHandlers;
        public StateInfo[] m_StateInfoArray;
        public ECodingType m_smCodingType;
        public bool m_hasStates = false;
        public Type m_stateEnumType;
        public string m_Name; // debugging only
        public static Hashtable m_smClassInfoMap = new Hashtable();
    }

    public class StateInfo
    {
        public StateInfo()
        {
            m_transitions = new Hashtable();
        }

        public MethodInfo m_entryMethod;
        public MethodInfo m_exitMethod;
        public MethodInfo m_defaultTransitionMethod;
        public Hashtable m_transitions;
        public string m_name; // for debugging purposes only
    }

    public enum ECodingType
    {
        Automatic,
        WithAttributes
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class SMCodingAttribute : System.Attribute
    {
        private ECodingType m_CodingType;
        public SMCodingAttribute(ECodingType codingType)
        {
            m_CodingType = codingType;
        }
        public ECodingType CodingType
        {
            get
            {
                return m_CodingType;
            }
        }
    }

    // [AttributeUsage(AttributeTargets.Field)]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SMStateAttribute : Attribute
    {
        public SMStateAttribute()
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SMEventHandlerAttribute : Attribute
    {
        public SMEventHandlerAttribute()
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SMNoHandlerAttribute : Attribute
    {
        public SMNoHandlerAttribute()
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SMTransitionHandlerAttribute : Attribute
    {
        public SMTransitionHandlerAttribute(string fromState)
        {
            m_fromState = fromState;
        }

        public string FromState
        {
            get
            {
                return m_fromState;
            }
        }

        private string m_fromState;
    }

    public enum EStateHandlerType
    {
        Entry,
        Exit
    };

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SMStateHandlerAttribute : Attribute
    {
        public SMStateHandlerAttribute(
            string state,
            EStateHandlerType handlerType)
        {
            m_state = state;
            m_handlerType = handlerType;
        }

        public string State
        {
            get
            {
                return m_state;
            }
        }

        public EStateHandlerType HandlerType
        {
            get
            {
                return m_handlerType;
            }
        }

        private string m_state;
        private EStateHandlerType m_handlerType;
    }

    [Serializable]
    public class UnhandledEventException : ApplicationException
    {
        public UnhandledEventException()
        {
        }
        public UnhandledEventException(string message)
            : base(message)
        {
        }
        public UnhandledEventException(string message, Exception inner)
            : base(message, inner)
        {
        }

        public UnhandledEventException(System.Runtime.Serialization.SerializationInfo si, System.Runtime.Serialization.StreamingContext sc)
            : base(si, sc)
        {
        }

    }



}
