/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Security.Policy;
using System.Reflection;
using System.Globalization;
using System.Xml;
using libsecondlife;
using log4net;
using Nini.Config;
using Amib.Threading;
using OpenSim.Framework;
using OpenSim.Region.Environment;
using OpenSim.Region.Environment.Scenes;
using OpenSim.Region.Environment.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.ScriptEngine.Shared.CodeTools;
using OpenSim.Region.ScriptEngine.Interfaces;

namespace OpenSim.Region.ScriptEngine.Shared.Instance
{
    public class ScriptInstance : IScriptInstance
    {
        private IScriptEngine m_Engine;
        private IScriptWorkItem m_CurrentResult=null;
        private Queue m_EventQueue = new Queue(32);
        private bool m_RunEvents = false;
        private LLUUID m_ItemID;
        private uint m_LocalID;
        private LLUUID m_ObjectID;
        private LLUUID m_AssetID;
        private IScript m_Script;
        private LLUUID m_AppDomain;
        private DetectParams[] m_DetectParams;
        private bool m_TimerQueued;
        private DateTime m_EventStart;
        private bool m_InEvent;
        private string m_PrimName;
        private string m_ScriptName;
        private string m_Assembly;
        private int m_StartParam = 0;
        private string m_CurrentEvent = String.Empty;
        private bool m_InSelfDelete = false;
        private int m_MaxScriptQueue;

        private Dictionary<string,IScriptApi> m_Apis = new Dictionary<string,IScriptApi>();

        // Script state
        private string m_State="default";

        public Object[] PluginData = new Object[0];

        public bool Running
        {
            get { return m_RunEvents; }
            set { m_RunEvents = value; }
        }

        public string State
        {
            get { return m_State; }
            set { m_State = value; }
        }

        public IScriptEngine Engine
        {
            get { return m_Engine; }
        }

        public LLUUID AppDomain
        {
            get { return m_AppDomain; }
            set { m_AppDomain = value; }
        }

        public string PrimName
        {
            get { return m_PrimName; }
        }

        public string ScriptName
        {
            get { return m_ScriptName; }
        }

        public LLUUID ItemID
        {
            get { return m_ItemID; }
        }

        public LLUUID ObjectID
        {
            get { return m_ObjectID; }
        }

        public uint LocalID
        {
            get { return m_LocalID; }
        }

        public LLUUID AssetID
        {
            get { return m_AssetID; }
        }

        public Queue EventQueue
        {
            get { return m_EventQueue; }
        }

        public void ClearQueue()
        {
            m_TimerQueued = false;
            m_EventQueue.Clear();
        }

        public int StartParam
        {
            get { return m_StartParam; }
            set { m_StartParam = value; }
        }

        public ScriptInstance(IScriptEngine engine, uint localID,
                LLUUID objectID, LLUUID itemID, LLUUID assetID, string assembly,
                AppDomain dom, string primName, string scriptName,
                int startParam, bool postOnRez, StateSource stateSource,
                int maxScriptQueue)
        {
            m_Engine = engine;

            m_LocalID = localID;
            m_ObjectID = objectID;
            m_ItemID = itemID;
            m_AssetID = assetID;
            m_PrimName = primName;
            m_ScriptName = scriptName;
            m_Assembly = assembly;
            m_StartParam = startParam;
            m_MaxScriptQueue = maxScriptQueue;

            ApiManager am = new ApiManager();

            SceneObjectPart part=engine.World.GetSceneObjectPart(localID);
            if (part == null)
            {
                engine.Log.Error("[Script] SceneObjectPart unavailable. Script NOT started.");
                return;
            }

            foreach (string api in am.GetApis())
            {
                m_Apis[api] = am.CreateApi(api);
                m_Apis[api].Initialize(engine, part, localID, itemID);
            }

            try
            {
                m_Script = (IScript)dom.CreateInstanceAndUnwrap(
                    Path.GetFileNameWithoutExtension(assembly),
                    "SecondLife.Script");
            }
            catch (Exception e)
            {
                m_Engine.Log.ErrorFormat("[Script] Error loading assembly {0}\n"+e.ToString(), assembly);
            }

            try
            {
                foreach (KeyValuePair<string,IScriptApi> kv in m_Apis)
                {
                    m_Script.InitApi(kv.Key, kv.Value);
                }

//                m_Engine.Log.Debug("[Script] Script instance created");

                part.SetScriptEvents(m_ItemID,
                                     (int)m_Script.GetStateEventFlags(State));
            }
            catch (Exception e)
            {
                m_Engine.Log.Error("[Script] Error loading script instance\n"+e.ToString());
                return;
            }

            string savedState = Path.Combine(Path.GetDirectoryName(assembly),
                    m_ItemID.ToString() + ".state");
            if (File.Exists(savedState))
            {
                string xml = String.Empty;

                try
                {
                    FileInfo fi = new FileInfo(savedState);
                    int size=(int)fi.Length;
                    if (size < 512000)
                    {
                        using (FileStream fs = File.Open(savedState,
                                                         FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            System.Text.ASCIIEncoding enc =
                                new System.Text.ASCIIEncoding();

                            Byte[] data = new Byte[size];
                            fs.Read(data, 0, size);

                            xml = enc.GetString(data);

                            ScriptSerializer.Deserialize(xml, this);

                            AsyncCommandManager async = (AsyncCommandManager)m_Engine.AsyncCommands;
                            async.CreateFromData(
                                m_LocalID, m_ItemID, m_ObjectID,
                                PluginData);

                            m_Engine.Log.DebugFormat("[Script] Successfully retrieved state for script {0}.{1}", m_PrimName, m_ScriptName);

                            if (m_RunEvents)
                            {
                                m_RunEvents = false;
                                Start();
                                if (postOnRez)
                                    PostEvent(new EventParams("on_rez",
                                        new Object[] {new LSL_Types.LSLInteger(startParam)}, new DetectParams[0]));
                            }

                            // we get new rez events on sim restart, too
                            // but if there is state, then we fire the change
                            // event
                            if (stateSource == StateSource.NewRez)
                            {
//                                m_Engine.Log.Debug("[Script] Posted changed(CHANGED_REGION_RESTART) to script");
                                PostEvent(new EventParams("changed",
                                    new Object[] {new LSL_Types.LSLInteger(256)}, new DetectParams[0]));
                            }
                        }
                    }
                    else
                    {
                        m_Engine.Log.Error("[Script] Unable to load script state: Memory limit exceeded");
                        Start();
                        PostEvent(new EventParams("state_entry",
                                                   new Object[0], new DetectParams[0]));
                        if (postOnRez)
                            PostEvent(new EventParams("on_rez",
                                new Object[] {new LSL_Types.LSLInteger(startParam)}, new DetectParams[0]));

                    }
                }
                catch (Exception e)
                {
                    m_Engine.Log.ErrorFormat("[Script] Unable to load script state from xml: {0}\n"+e.ToString(), xml);
                    Start();
                    PostEvent(new EventParams("state_entry",
                                               new Object[0], new DetectParams[0]));
                    if (postOnRez)
                        PostEvent(new EventParams("on_rez",
                                new Object[] {new LSL_Types.LSLInteger(startParam)}, new DetectParams[0]));
                }
            }
            else
            {
//                m_Engine.Log.ErrorFormat("[Script] Unable to load script state, file not found");
                Start();
                PostEvent(new EventParams("state_entry",
                                           new Object[0], new DetectParams[0]));

                if (postOnRez)
                    PostEvent(new EventParams("on_rez",
                            new Object[] {new LSL_Types.LSLInteger(startParam)}, new DetectParams[0]));
            }
        }

        public void RemoveState()
        {
            string savedState = Path.Combine(Path.GetDirectoryName(m_Assembly),
                    m_ItemID.ToString() + ".state");

            try
            {
                File.Delete(savedState);
            }
            catch(Exception)
            {
            }
        }

        public void VarDump(Dictionary<string, object> vars)
        {
            Console.WriteLine("Variable dump for script {0}", m_ItemID.ToString());
            foreach (KeyValuePair<string, object> v in vars)
            {
                Console.WriteLine("Variable: {0} = '{1}'", v. Key,
                                  v.Value.ToString());
            }
        }

        public void Start()
        {
            lock (m_EventQueue)
            {
                if (Running)
                    return;

                m_RunEvents = true;

                if (m_EventQueue.Count > 0)
                {
                    if (m_CurrentResult == null)
                        m_CurrentResult = m_Engine.QueueEventHandler(this);
                    else
                        m_Engine.Log.Error("[Script] Tried to start a script that was already queued");
                }
            }
        }

        public bool Stop(int timeout)
        {
            IScriptWorkItem result;

            lock (m_EventQueue)
            {
                if (!Running)
                    return true;

                if (m_CurrentResult == null)
                {
                    m_RunEvents = false;
                    return true;
                }

                if (m_CurrentResult.Cancel())
                {
                    m_CurrentResult = null;
                    m_RunEvents = false;
                    return true;
                }

                result = m_CurrentResult;
                m_RunEvents = false;
            }

            if (result.Wait(new TimeSpan((long)timeout * 100000)))
            {
                return true;
            }

            lock (m_EventQueue)
            {
                result = m_CurrentResult;
            }

            if (result == null)
                return true;

            if (!m_InSelfDelete)
                result.Abort();

            lock (m_EventQueue)
            {
                m_CurrentResult = null;
            }

            return true;
        }

        public void SetState(string state)
        {
            PostEvent(new EventParams("state_exit", new Object[0],
                                       new DetectParams[0]));
            PostEvent(new EventParams("state", new Object[] { state },
                                       new DetectParams[0]));
            PostEvent(new EventParams("state_entry", new Object[0],
                                       new DetectParams[0]));
        }

        public void PostEvent(EventParams data)
        {
//            m_Engine.Log.DebugFormat("[Script] Posted event {2} in state {3} to {0}.{1}",
//                        m_PrimName, m_ScriptName, data.EventName, m_State);

            if (!Running)
                return;

            lock (m_EventQueue)
            {
                if (m_EventQueue.Count >= m_MaxScriptQueue)
                    return;

                m_EventQueue.Enqueue(data);
                if (data.EventName == "timer")
                {
                    if (m_TimerQueued)
                        return;
                    m_TimerQueued = true;
                }

                if (!m_RunEvents)
                    return;

                if (m_CurrentResult == null)
                {
                    m_CurrentResult = m_Engine.QueueEventHandler(this);
                }
            }
        }

        public object EventProcessor()
        {
            EventParams data = null;

            lock (m_EventQueue)
            {
                data = (EventParams) m_EventQueue.Dequeue();
                if (data == null) // Shouldn't happen
                {
                    m_CurrentResult = null;
                    return 0;
                }
                if (data.EventName == "timer")
                    m_TimerQueued = false;
            }

            m_DetectParams = data.DetectParams;

            if (data.EventName == "state") // Hardcoded state change
            {
//                m_Engine.Log.DebugFormat("[Script] Script {0}.{1} state set to {2}",
//                        m_PrimName, m_ScriptName, data.Params[0].ToString());
                m_State=data.Params[0].ToString();
                AsyncCommandManager async = (AsyncCommandManager)m_Engine.AsyncCommands;
                async.RemoveScript(
                    m_LocalID, m_ItemID);

                SceneObjectPart part = m_Engine.World.GetSceneObjectPart(
                    m_LocalID);
                if (part != null)
                {
                    part.SetScriptEvents(m_ItemID,
                                         (int)m_Script.GetStateEventFlags(State));
                }
            }
            else
            {
                SceneObjectPart part = m_Engine.World.GetSceneObjectPart(
                    m_LocalID);
//                m_Engine.Log.DebugFormat("[Script] Delivered event {2} in state {3} to {0}.{1}",
//                        m_PrimName, m_ScriptName, data.EventName, m_State);

                try
                {
                    m_CurrentEvent = data.EventName;
                    m_EventStart = DateTime.Now;
                    m_InEvent = true;

                    m_Script.ExecuteEvent(State, data.EventName, data.Params);

                    m_InEvent = false;
                    m_CurrentEvent = String.Empty;
                }
                catch (Exception e)
                {
                    m_InEvent = false;
                    m_CurrentEvent = String.Empty;

                    if (!(e is TargetInvocationException) || (!(e.InnerException is EventAbortException) && (!(e.InnerException is SelfDeleteException))))
                    {
                        if (e is System.Threading.ThreadAbortException)
                        {
                            lock (m_EventQueue)
                            {
                                if ((m_EventQueue.Count > 0) && m_RunEvents)
                                {
                                    m_CurrentResult=m_Engine.QueueEventHandler(this);
                                }
                                else
                                {
                                    m_CurrentResult = null;
                                }
                            }

                            m_DetectParams = null;

                            return 0;
                        }

                        try
                        {
                            // DISPLAY ERROR INWORLD
                            string text = "Runtime error:\n" + e.ToString();
                            if (text.Length > 1400)
                                text = text.Substring(0, 1400);
                            m_Engine.World.SimChat(Helpers.StringToField(text),
                                                   ChatTypeEnum.DebugChannel, 2147483647,
                                                   part.AbsolutePosition,
                                                   part.Name, part.UUID, false);
                        }
                        catch (Exception e2) // LEGIT: User Scripting
                        {
                            m_Engine.Log.Error("[Script]: "+
                                               "Error displaying error in-world: " +
                                               e2.ToString());
                            m_Engine.Log.Error("[Script]: " +
                                               "Errormessage: Error compiling script:\r\n" +
                                               e.ToString());
                        }
                    }
                    else if ((e is TargetInvocationException) && (e.InnerException is SelfDeleteException))
                    {
                        m_InSelfDelete = true;
                        if (part != null && part.ParentGroup != null)
                            m_Engine.World.DeleteSceneObject(part.ParentGroup);
                    }
                }
            }

            lock (m_EventQueue)
            {
                if ((m_EventQueue.Count > 0) && m_RunEvents)
                {
                    m_CurrentResult = m_Engine.QueueEventHandler(this);
                }
                else
                {
                    m_CurrentResult = null;
                }
            }

            m_DetectParams = null;

            return 0;
        }

        public int EventTime()
        {
            if (!m_InEvent)
                return 0;

            return (DateTime.Now - m_EventStart).Seconds;
        }

        public void ResetScript()
        {
            bool running = Running;

            RemoveState();

            Stop(0);
            SceneObjectPart part=m_Engine.World.GetSceneObjectPart(m_LocalID);
            part.GetInventoryItem(m_ItemID).PermsMask = 0;
            part.GetInventoryItem(m_ItemID).PermsGranter = LLUUID.Zero;
            AsyncCommandManager async = (AsyncCommandManager)m_Engine.AsyncCommands;
            async.RemoveScript(m_LocalID, m_ItemID);
            m_EventQueue.Clear();
            m_Script.ResetVars();
            m_State = "default";
            if (running)
                Start();
            PostEvent(new EventParams("state_entry",
                    new Object[0], new DetectParams[0]));
        }

        public void ApiResetScript()
        {
            // bool running = Running;

            RemoveState();

            m_Script.ResetVars();
            SceneObjectPart part=m_Engine.World.GetSceneObjectPart(m_LocalID);
            part.GetInventoryItem(m_ItemID).PermsMask = 0;
            part.GetInventoryItem(m_ItemID).PermsGranter = LLUUID.Zero;
            AsyncCommandManager async = (AsyncCommandManager)m_Engine.AsyncCommands;
            async.RemoveScript(m_LocalID, m_ItemID);
            if (m_CurrentEvent != "state_entry")
            {
                PostEvent(new EventParams("state_entry",
                        new Object[0], new DetectParams[0]));
            }
        }

        public Dictionary<string, object> GetVars()
        {
            return m_Script.GetVars();
        }

        public void SetVars(Dictionary<string, object> vars)
        {
            m_Script.SetVars(vars);
        }

        public DetectParams GetDetectParams(int idx)
        {
            if (idx < 0 || idx >= m_DetectParams.Length)
                return null;

            return m_DetectParams[idx];
        }

        public LLUUID GetDetectID(int idx)
        {
            if (idx < 0 || idx >= m_DetectParams.Length)
                return LLUUID.Zero;

            return m_DetectParams[idx].Key;
        }

        public void SaveState(string assembly)
        {
            AsyncCommandManager async = (AsyncCommandManager)m_Engine.AsyncCommands;
            PluginData = async.GetSerializationData(m_ItemID);

            string xml = ScriptSerializer.Serialize(this);

            try
            {
                FileStream fs = File.Create(Path.Combine(Path.GetDirectoryName(assembly), m_ItemID.ToString() + ".state"));
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                Byte[] buf = enc.GetBytes(xml);
                fs.Write(buf, 0, buf.Length);
                fs.Close();
            }
            catch(Exception e)
            {
                Console.WriteLine("Unable to save xml\n"+e.ToString());
            }
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(assembly), m_ItemID.ToString() + ".state")))
            {
                throw new Exception("Completed persistence save, but no file was created");
            }
        }
    }
}