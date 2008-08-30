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
using System.Collections;
using System.Collections.Generic;
using libsecondlife;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Interfaces;

namespace OpenSim.Region.ScriptEngine.Interfaces
{
    public enum StateSource
    {
        NewRez = 0,
        PrimCrossing = 1,
        AttachmentCrossing = 2
    }

    public interface IScriptWorkItem
    {
        bool Cancel();
        void Abort();
        bool Wait(TimeSpan t);
    }

    public interface IScriptInstance
    {
        bool Running { get; set; }
        string State { get; set; }
        IScriptEngine Engine { get; }
        LLUUID AppDomain { get; set; }
        string PrimName { get; }
        string ScriptName { get; }
        LLUUID ItemID { get; }
        LLUUID ObjectID { get; }
        uint LocalID { get; }
        LLUUID AssetID { get; }
        Queue EventQueue { get; }

        void ClearQueue();
        int StartParam { get; set; }

        void RemoveState();

        void Start();
        bool Stop(int timeout);
        void SetState(string state);

        void PostEvent(EventParams data);
        object EventProcessor();

        int EventTime();
        void ResetScript();
        void ApiResetScript();
        Dictionary<string, object> GetVars();
        void SetVars(Dictionary<string, object> vars);
        DetectParams GetDetectParams(int idx);
        LLUUID GetDetectID(int idx);
        void SaveState(string assembly);
    }
}