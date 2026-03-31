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
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using log4net;

namespace WebRtcVoice
{
    public class WebRtcJanusService : ServiceBase, IWebRtcVoiceService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[JANUS WEBRTC SERVICE]";

        private readonly IConfigSource _Config;
        private bool _Enabled = false;

        private string _JanusServerURI = String.Empty;
        private string _JanusAPIToken = String.Empty;
        private string _JanusAdminURI = String.Empty;
        private string _JanusAdminToken = String.Empty;

        private bool _JanusDebug = false;
        private bool _MessageDetails = false;
        // Maximum ICE candidates accepted from one VoiceSignalingRequest call.
        // <= 0 means no limit.
        private int _MaxSignalingCandidatesPerRequest = 20;
        // Delay between a disconnect and next join for same agent.
        private int _RejoinCooldownMs = 250;

        private readonly ConcurrentDictionary<UUID, DateTime> _LastDisconnectByAgent = new ConcurrentDictionary<UUID, DateTime>();
        private long _VoiceFlowCounter;

        // An extra "viewer session" that is created initially. Used to verify the service
        //     is working and for a handle for the console commands.
        private JanusViewerSession _ViewerSession;

        public WebRtcJanusService(IConfigSource pConfig) : base(pConfig)
        {
            WebRtcDebugControl.ApplyFromConfig(pConfig);

            Assembly assembly = Assembly.GetExecutingAssembly();
            string version = assembly.GetName().Version?.ToString() ?? "unknown";

            _log.DebugFormat("{0} WebRtcJanusService version {1}", LogHeader, version);
            _Config = pConfig;
            IConfig webRtcVoiceConfig = _Config.Configs["WebRtcVoice"];

            if (webRtcVoiceConfig is not null)
            {
                _Enabled = webRtcVoiceConfig.GetBoolean("Enabled", false);
                IConfig janusConfig = _Config.Configs["JanusWebRtcVoice"];
                if (_Enabled && janusConfig is not null)
                {
                    _JanusServerURI = janusConfig.GetString("JanusGatewayURI", String.Empty);
                    _JanusAPIToken = janusConfig.GetString("APIToken", String.Empty);
                    _JanusAdminURI = janusConfig.GetString("JanusGatewayAdminURI", String.Empty);
                    _JanusAdminToken = janusConfig.GetString("AdminAPIToken", String.Empty);
                    // Debugging options
                    _JanusDebug = janusConfig.GetBoolean("JanusDebug", false);
                    _MessageDetails = janusConfig.GetBoolean("MessageDetails", false);
                    _MaxSignalingCandidatesPerRequest = janusConfig.GetInt("MaxSignalingCandidatesPerRequest", 20);
                    _RejoinCooldownMs = janusConfig.GetInt("RejoinCooldownMs", 250);

                    if (_MaxSignalingCandidatesPerRequest < 0)
                    {
                        _log.WarnFormat("{0} MaxSignalingCandidatesPerRequest < 0 ({1}), using 0 (unlimited)",
                                LogHeader, _MaxSignalingCandidatesPerRequest);
                        _MaxSignalingCandidatesPerRequest = 0;
                    }

                    if (_RejoinCooldownMs < 0)
                    {
                        _log.WarnFormat("{0} RejoinCooldownMs < 0 ({1}), using 0", LogHeader, _RejoinCooldownMs);
                        _RejoinCooldownMs = 0;
                    }

                    if (String.IsNullOrEmpty(_JanusServerURI) || String.IsNullOrEmpty(_JanusAPIToken) ||
                        String.IsNullOrEmpty(_JanusAdminURI) || String.IsNullOrEmpty(_JanusAdminToken))
                    {
                        _log.ErrorFormat("{0} JanusWebRtcVoice configuration section missing required fields", LogHeader);
                        _Enabled = false;
                    }

                    if (_Enabled)
                    {
                        _log.DebugFormat("{0} Enabled", LogHeader);
                        StartConnectionToJanus();
                        RegisterConsoleCommands();
                    }
                }
                else
                {
                    _log.ErrorFormat("{0} No JanusWebRtcVoice configuration section", LogHeader);
                    _Enabled = false;
                }
            }
            else
            {
                _log.ErrorFormat("{0} No WebRtcVoice configuration section", LogHeader);
                _Enabled = false;
            }
        }

        // Start a thread to do the connection to the Janus server.
        // Here an initial session is created and then a handle to the audio bridge plugin
        //    is created for the console commands. Since webrtc PeerConnections that are created
        //    my Janus are per-session, the other sessions will be created by the viewer requests.
        private void StartConnectionToJanus()
        {
            _log.DebugFormat("{0} StartConnectionToJanus", LogHeader);
            Task.Run(async () =>
            {
                _ViewerSession = new JanusViewerSession(this);
                await ConnectToSessionAndAudioBridge(_ViewerSession);
            });
        }

        private async Task ConnectToSessionAndAudioBridge(JanusViewerSession pViewerSession)
        {
            JanusSession janusSession = new JanusSession(_JanusServerURI, _JanusAPIToken, _JanusAdminURI, _JanusAdminToken, _JanusDebug, _MessageDetails);
            if (await janusSession.CreateSession())
            {
                _log.DebugFormat("{0} JanusSession created", LogHeader);
                janusSession.OnDisconnect += Handle_Disconnect;

                // Once the session is created, create a handle to the plugin for rooms
                JanusAudioBridge audioBridge = new JanusAudioBridge(janusSession);
                janusSession.AddPlugin(audioBridge);

                pViewerSession.VoiceServiceSessionId = janusSession.SessionId;
                pViewerSession.Session = janusSession;
                pViewerSession.AudioBridge = audioBridge;

                janusSession.OnHangup += Handle_Hangup;

                if (await audioBridge.Activate(_Config))
                {
                    _log.DebugFormat("{0} AudioBridgePluginHandle created", LogHeader);
                    // Requests through the capabilities will create rooms
                }
                else
                {
                    _log.ErrorFormat("{0} JanusPluginHandle not created", LogHeader);
                }
            }
            else
            {
                _log.ErrorFormat("{0} JanusSession not created", LogHeader);
            }   
        }

        private void Handle_Hangup(EventResp pResp)
        {
            if (pResp is not null)
            {
                var sessionId = pResp.sessionId;
                string reason = pResp.RawBody.TryGetString("reason", out string r) ? r : String.Empty;
                if (_MessageDetails)
                {
                    _log.DebugFormat("{0} Handle_Hangup: {1}, sessionId={2}", LogHeader, pResp.RawBody.ToString(), sessionId);
                }
                else
                {
                    _log.DebugFormat("{0} Handle_Hangup: sessionId={1}, reason={2}", LogHeader, sessionId, reason);
                }
                if (VoiceViewerSession.TryGetViewerSessionByVSSessionId(sessionId, out IVoiceViewerSession viewerSession))
                {
                    // A Janus hangup can happen during a normal room switch/re-offer cycle.
                    // Keep the viewer session alive and only clear the per-call state.
                    if (viewerSession is JanusViewerSession janusViewerSession)
                    {
                        janusViewerSession.ParticipantId = 0;
                        janusViewerSession.Answer = null;
                        janusViewerSession.Offer = String.Empty;
                        janusViewerSession.OfferOrig = String.Empty;
                        janusViewerSession.Room = null;
                    }
                }
                else
                {
                    _log.DebugFormat("{0} Handle_Hangup: no session found. SessionId={1}", LogHeader, sessionId);
                }
            }
        }

        private void Handle_Disconnect(EventResp pResp)
        {
            if (pResp is null)
                return;

            string sessionId = pResp.sessionId;
            if (VoiceViewerSession.TryGetViewerSessionByVSSessionId(sessionId, out IVoiceViewerSession viewerSession))
            {
                DisconnectViewerSession(viewerSession as JanusViewerSession, "disconnect");
            }
            else
            {
                _log.DebugFormat("{0} Handle_Disconnect: no session found. SessionId={1}", LogHeader, sessionId);
            }
        }

        private static string FlowTag(long pFlowId, JanusViewerSession pViewerSession)
        {
            string vs = pViewerSession?.ViewerSessionID ?? "<none>";
            return String.Format("flow={0}, viewer_session={1}", pFlowId, vs);
        }

        private async Task EnforceRejoinCooldown(UUID pAgentId, JanusViewerSession pViewerSession, long pFlowId)
        {
            if (_RejoinCooldownMs <= 0)
                return;

            if (_LastDisconnectByAgent.TryGetValue(pAgentId, out DateTime lastDisconnectUtc))
            {
                int elapsedMs = (int)(DateTime.UtcNow - lastDisconnectUtc).TotalMilliseconds;
                int waitMs = _RejoinCooldownMs - elapsedMs;
                if (waitMs > 0)
                {
                    _log.DebugFormat("{0} ProvisionVoiceAccountRequest: applying rejoin cooldown {1}ms ({2})",
                            LogHeader, waitMs, FlowTag(pFlowId, pViewerSession));
                    await Task.Delay(waitMs);
                }
            }
        }

        // Disconnect the viewer session. This is called when the viewer logs out or hangs up.
        private void DisconnectViewerSession(JanusViewerSession pViewerSession, string pReason)
        {
            if (pViewerSession is not null)
            {
                if (!pViewerSession.TryStartDisconnect(pReason))
                {
                    _log.DebugFormat("{0} DisconnectViewerSession: duplicate disconnect suppressed. viewer_session={1}, reason={2}, firstReason={3}",
                            LogHeader, pViewerSession.ViewerSessionID, pReason, pViewerSession.DisconnectReason);
                    return;
                }

                int roomId = pViewerSession.Room is not null ? pViewerSession.Room.RoomId : 0;
                _LastDisconnectByAgent[pViewerSession.AgentId] = DateTime.UtcNow;
                _log.InfoFormat("{0} ProvisionVoiceAccountRequest: disconnected by {1}. agent={2}, scene={3}, room={4}, participant={5}, viewer_session={6}",
                        LogHeader, pReason, pViewerSession.AgentId, pViewerSession.RegionId, roomId, pViewerSession.ParticipantId, pViewerSession.ViewerSessionID);
                Task.Run(() =>
                {
                    VoiceViewerSession.RemoveViewerSession(pViewerSession.ViewerSessionID);
                    // No need to wait for the session to be shutdown
                    _ = pViewerSession.Shutdown();
                });
            }
        }   

        // The pRequest parameter is a straight conversion of the JSON request from the client.
        // This is the logic that takes the client's request and converts it into
        //     operations on rooms in the audio bridge.
        // IWebRtcVoiceService.ProvisionVoiceAccountRequest
        public async Task<OSDMap> ProvisionVoiceAccountRequest(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            OSDMap ret = null;
            string errorMsg = null;
            JanusViewerSession viewerSession = pSession as JanusViewerSession;
            long flowId = Interlocked.Increment(ref _VoiceFlowCounter);
            if (viewerSession is not null)
            {
                _log.DebugFormat("{0} ProvisionVoiceAccountRequest: begin ({1})", LogHeader, FlowTag(flowId, viewerSession));
                if (viewerSession.Session is null)
                {
                    // This is a new session so we must create a new session and handle to the audio bridge
                    await ConnectToSessionAndAudioBridge(viewerSession);
                }

                // TODO: need to keep count of users in a room to know when to close a room
                bool isLogout = pRequest.ContainsKey("logout") && pRequest["logout"].AsBoolean();
                if (isLogout)
                {
                    // The client is logging out. Disconnect the entire Janus viewer session.
                    DisconnectViewerSession(viewerSession, "logout");
                    return new OSDMap
                    {
                        { "response", "closed" }
                    };
                }

                // Get the parameters that select the room
                // To get here, voice_server_type has already been checked to be 'webrtc' and channel_type='local'
                int parcel_local_id = pRequest.ContainsKey("parcel_local_id") ? pRequest["parcel_local_id"].AsInteger() : JanusAudioBridge.REGION_ROOM_ID;
                string channel_id = pRequest.ContainsKey("channel_id") ? pRequest["channel_id"].AsString() : String.Empty;
                string channel_credentials = pRequest.ContainsKey("credentials") ? pRequest["credentials"].AsString() : String.Empty;
                string channel_type = pRequest["channel_type"].AsString();
                bool isSpatial = channel_type == "local";
                string voice_server_type = pRequest["voice_server_type"].AsString();

                _log.DebugFormat("{0} ProvisionVoiceAccountRequest: parcel_id={1} channel_id={2} channel_type={3} voice_server_type={4}", LogHeader, parcel_local_id, channel_id, channel_type, voice_server_type); 

                if (pRequest.ContainsKey("jsep") && pRequest["jsep"] is OSDMap jsep)
                {
                    await viewerSession.ProvisionLock.WaitAsync();
                    try
                    {
                    // The jsep is the SDP from the client. This is the client's request to connect to the audio bridge.
                    string jsepType = jsep["type"].AsString();
                    string jsepSdp = jsep["sdp"].AsString();
                    if (jsepType == "offer")
                    {
                        // The client is sending an offer. Find the right room and join it.
                        // _log.DebugFormat("{0} ProvisionVoiceAccountRequest: jsep type={1} sdp={2}", LogHeader, jsepType, jsepSdp);
                        JanusRoom previousRoom = viewerSession.Room;
                        JanusRoom selectedRoom = await viewerSession.AudioBridge.SelectRoom(pSceneID.ToString(),
                                                            channel_type, isSpatial, parcel_local_id, channel_id);
                        if (selectedRoom is null)
                        {
                            errorMsg = "room selection failed";
                            _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: room selection failed", LogHeader);
                        }
                        else {
                            if (previousRoom is not null && viewerSession.ParticipantId > 0)
                            {
                                _log.InfoFormat("{0} ProvisionVoiceAccountRequest: leaving existing participant {1} from room {2} before rejoin to room {3}",
                                        LogHeader, viewerSession.ParticipantId, previousRoom.RoomId, selectedRoom.RoomId);
                                await previousRoom.LeaveRoom(viewerSession);
                                viewerSession.ParticipantId = 0;
                            }

                            await EnforceRejoinCooldown(pUserID, viewerSession, flowId);

                            viewerSession.Room = selectedRoom;
                            viewerSession.Offer = jsepSdp;
                            viewerSession.OfferOrig = jsepSdp;
                            viewerSession.AgentId = pUserID;
                            if (await viewerSession.Room.JoinRoom(viewerSession))    
                            {
                                bool hasAnswerSdp =
                                        viewerSession.Answer is not null &&
                                        viewerSession.Answer.TryGetString("sdp", out string answerSdp) &&
                                        !String.IsNullOrEmpty(answerSdp);

                                if (!hasAnswerSdp)
                                {
                                    errorMsg = "JoinRoom without valid jsep/sdp";
                                    _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: JoinRoom returned without valid answer SDP. agent={1}, scene={2}, room={3}, participant={4}",
                                            LogHeader, pUserID, pSceneID, viewerSession.Room.RoomId, viewerSession.ParticipantId);
                                }
                                else
                                {
                                    viewerSession.RegionId = pSceneID;
                                    _log.InfoFormat("{0} ProvisionVoiceAccountRequest: connected. agent={1}, scene={2}, room={3}, participant={4}, viewer_session={5}",
                                            LogHeader, pUserID, pSceneID, viewerSession.Room.RoomId, viewerSession.ParticipantId, viewerSession.ViewerSessionID);
                                    ret = new OSDMap
                                    {
                                        { "jsep", viewerSession.Answer },
                                        { "viewer_session", viewerSession.ViewerSessionID }
                                    };
                                }
                            }
                            else
                            {
                                errorMsg = "JoinRoom failed";
                                _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: JoinRoom failed", LogHeader);
                            }
                        }
                    }
                    else
                    {
                        errorMsg = "jsep type not offer";
                        _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: jsep type={1} not offer", LogHeader, jsepType);
                    }
                    }
                    finally
                    {
                        viewerSession.ProvisionLock.Release();
                    }
                }
                else
                {
                    errorMsg = "no jsep";
                    _log.DebugFormat("{0} ProvisionVoiceAccountRequest: no jsep. req={1}", LogHeader, pRequest.ToString());
                }
            }
            else
            {
                errorMsg = "viewersession not JanusViewerSession";
                _log.ErrorFormat("{0} ProvisionVoiceAccountRequest: viewersession not JanusViewerSession", LogHeader);
            }

            if (!String.IsNullOrEmpty(errorMsg) && ret is null)
            {
                // The provision failed so build an error messgage to return
                ret = new OSDMap
                {
                    { "response", "failed" },
                    { "error", errorMsg }
                };
                _log.WarnFormat("{0} ProvisionVoiceAccountRequest: failed ({1}) error={2}", LogHeader, FlowTag(flowId, viewerSession), errorMsg);
            }
            else
            {
                _log.DebugFormat("{0} ProvisionVoiceAccountRequest: end ({1})", LogHeader, FlowTag(flowId, viewerSession));
            }

            return ret;
        }

        // IWebRtcVoiceService.VoiceAccountBalanceRequest
        public async Task<OSDMap> VoiceSignalingRequest(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            OSDMap ret = null;
            JanusViewerSession viewerSession = pSession as JanusViewerSession;
            JanusMessageResp resp = null;
            long flowId = Interlocked.Increment(ref _VoiceFlowCounter);
            if (viewerSession is not null)
            {
                _log.DebugFormat("{0} VoiceSignalingRequest: begin ({1})", LogHeader, FlowTag(flowId, viewerSession));
                // The request should be an array of candidates
                if (pRequest.ContainsKey("candidate") && pRequest["candidate"] is OSDMap candidate)
                {
                    if (candidate.ContainsKey("completed") && candidate["completed"].AsBoolean())
                    {
                        // The client has finished sending candidates
                        resp = await viewerSession.Session.TrickleCompleted(viewerSession);
                        _log.DebugFormat("{0} VoiceSignalingRequest: candidate completed", LogHeader);
                    }
                    else
                    {
                        OSDArray candidatesArray = new OSDArray
                        {
                            new OSDMap()
                            {
                                { "candidate", candidate.ContainsKey("candidate") ? candidate["candidate"].AsString() : String.Empty },
                                { "sdpMid", candidate.ContainsKey("sdpMid") ? candidate["sdpMid"].AsString() : String.Empty },
                                { "sdpMLineIndex", candidate.ContainsKey("sdpMLineIndex") ? candidate["sdpMLineIndex"].AsLong() : 0 }
                            }
                        };
                        resp = await viewerSession.Session.TrickleCandidates(viewerSession, candidatesArray);
                        _log.DebugFormat("{0} VoiceSignalingRequest: single candidate", LogHeader);
                    }
                }
                else if (pRequest.ContainsKey("candidates") && pRequest["candidates"] is OSDArray candidates)
                {
                    OSDArray candidatesArray = new OSDArray();
                    int sourceCount = candidates.Count;
                    int candidateLimit = _MaxSignalingCandidatesPerRequest;
                    foreach (OSDMap cand in candidates)
                    {
                        if (candidateLimit > 0 && candidatesArray.Count >= candidateLimit)
                            break;

                        candidatesArray.Add(new OSDMap() {
                            { "candidate", cand["candidate"].AsString() },
                            { "sdpMid", cand["sdpMid"].AsString() },
                            { "sdpMLineIndex", cand["sdpMLineIndex"].AsLong() }
                        });
                    }
                    resp = await viewerSession.Session.TrickleCandidates(viewerSession, candidatesArray);
                    if (candidateLimit > 0 && sourceCount > candidatesArray.Count)
                    {
                        _log.WarnFormat("{0} VoiceSignalingRequest: capped candidates {1}/{2} (MaxSignalingCandidatesPerRequest={3})",
                                LogHeader, candidatesArray.Count, sourceCount, candidateLimit);
                    }
                    else
                    {
                        _log.DebugFormat("{0} VoiceSignalingRequest: {1} candidates", LogHeader, candidatesArray.Count);
                    }
                }
                else
                {
                    _log.ErrorFormat("{0} VoiceSignalingRequest: no 'candidate' or 'candidates'", LogHeader);
                }
            }
            if (resp is null)
            {
                _log.ErrorFormat("{0} VoiceSignalingRequest: no response so returning error", LogHeader);
                ret = new OSDMap
                {
                    { "response", "error" }
                };
            }
            else
            {
                ret = resp.RawBody;
            }
            _log.DebugFormat("{0} VoiceSignalingRequest: end ({1})", LogHeader, FlowTag(flowId, viewerSession));
            return ret;
        }

        // This module should not be invoked with this signature
        // IWebRtcVoiceService.ProvisionVoiceAccountRequest
        public Task<OSDMap> ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            throw new NotImplementedException();
        }

        // This module should not be invoked with this signature
        // IWebRtcVoiceService.VoiceSignalingRequest
        public Task<OSDMap> VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            throw new NotImplementedException();
        }

        // The viewer session object holds all the connection information to Janus.
        // IWebRtcVoiceService.CreateViewerSession
        public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            return new JanusViewerSession(this)
            {
                AgentId = pUserID,
                RegionId = pSceneID
            };
        }

        // ======================================================================================================
        private void RegisterConsoleCommands()
        {
            if (_Enabled) {
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus info",
                    "janus info",
                    "Show Janus server information in human-readable form (use 'janus info json' for raw JSON)",
                    HandleJanusInfo);
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus show",
                    "janus show",
                    "Alias for 'janus info'",
                    HandleJanusShow);
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus list rooms",
                    "janus list rooms",
                    "List the rooms on the Janus server",
                    HandleJanusListRooms);
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus list sessions",
                    "janus list sessions",
                    "List active Janus sessions (admin API)",
                    HandleJanusListSessions);
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus list",
                    "janus list",
                    "List Janus rooms and sessions (shortcut for diagnostics)",
                    HandleJanusList);
                MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus room",
                    "janus room <roomId>",
                    "Show one room with participant details",
                    HandleJanusRoom);
                // List rooms
                // List participants in a room
            }
        }

        private void HandleJanusList(string module, string[] cmdparms)
        {
            WriteOut("janus list: showing rooms then sessions");
            HandleJanusListRooms(module, cmdparms);
            HandleJanusListSessions(module, cmdparms);
        }

        private async void HandleJanusInfo(string module, string[] cmdparms)
        {
            if (_ViewerSession is not null && _ViewerSession.Session is not null)
            {
                WriteOut("{0} Janus session: {1}", LogHeader, _ViewerSession.Session.SessionId);
                string infoURI = _ViewerSession.Session.JanusServerURI + "/info";
                var resp = await _ViewerSession.Session.GetFromJanus(infoURI);
                if (resp is null)
                {
                    WriteOut("{0} Failed to query Janus /info", LogHeader);
                    return;
                }

                bool requestJson = cmdparms is not null
                                   && cmdparms.Length > 2
                                   && cmdparms[2].Equals("json", StringComparison.OrdinalIgnoreCase);

                if (requestJson)
                {
                    MainConsole.Instance.Output(resp.ToJson());
                    return;
                }

                OSDMap info = resp.RawBody;
                if (info is null || info.Count == 0)
                {
                    WriteOut("{0} Janus /info returned no data", LogHeader);
                    return;
                }

                PrintJanusInfo(info, "janus info json");
            }
        }

        private async void HandleJanusShow(string module, string[] cmdparms)
        {
            if (_ViewerSession is not null && _ViewerSession.Session is not null)
            {
                WriteOut("{0} Janus session: {1}", LogHeader, _ViewerSession.Session.SessionId);
                string infoURI = _ViewerSession.Session.JanusServerURI + "/info";
                var resp = await _ViewerSession.Session.GetFromJanus(infoURI);
                if (resp is null)
                {
                    WriteOut("{0} Failed to query Janus /info", LogHeader);
                    return;
                }

                OSDMap info = resp.RawBody;
                if (info is null || info.Count == 0)
                {
                    WriteOut("{0} Janus /info returned no data", LogHeader);
                    return;
                }

                PrintJanusInfo(info, "janus info json");
            }
        }

        private void PrintJanusInfo(OSDMap info, string jsonHintCommand)
        {
            WriteOut("");
            WriteOut("Janus Server Info");
            WriteOut("  Name            : {0}", GetMapString(info, "name"));
            WriteOut("  Server Name     : {0}", GetMapString(info, "server-name"));
            WriteOut("  Version         : {0} ({1})", GetMapString(info, "version_string"), GetMapString(info, "version"));
            WriteOut("  Author          : {0}", GetMapString(info, "author"));
            WriteOut("  Local IP        : {0}", GetMapString(info, "local-ip"));
            WriteOut("  New Sessions    : {0}", GetMapString(info, "accepting-new-sessions"));

            WriteOut("");
            WriteOut("Session / Timeouts");
            WriteOut("  session-timeout : {0}", GetMapString(info, "session-timeout"));
            WriteOut("  reclaim-timeout : {0}", GetMapString(info, "reclaim-session-timeout"));
            WriteOut("  candidates-time : {0}", GetMapString(info, "candidates-timeout"));

            WriteOut("");
            WriteOut("ICE / Network");
            WriteOut("  ice-lite        : {0}", GetMapString(info, "ice-lite"));
            WriteOut("  ice-tcp         : {0}", GetMapString(info, "ice-tcp"));
            WriteOut("  full-trickle    : {0}", GetMapString(info, "full-trickle"));
            WriteOut("  mdns-enabled    : {0}", GetMapString(info, "mdns-enabled"));
            WriteOut("  dtls-mtu        : {0}", GetMapString(info, "dtls-mtu"));

            WriteOut("");
            WriteOut("Security");
            WriteOut("  api_secret      : {0}", GetMapString(info, "api_secret"));
            WriteOut("  auth_token      : {0}", GetMapString(info, "auth_token"));

            WriteOut("");
            WriteOut("Transports");
            PrintNamedModuleMap(info, "transports");

            WriteOut("");
            WriteOut("Plugins");
            PrintNamedModuleMap(info, "plugins");

            WriteOut("");
            WriteOut("Tip: use '{0}' for full JSON output", jsonHintCommand);
        }

        private static string GetMapString(OSDMap map, string key)
        {
            if (map is not null && map.TryGetValue(key, out OSD value) && value is not null)
            {
                return value.AsString();
            }
            return "-";
        }

        private void PrintNamedModuleMap(OSDMap root, string key)
        {
            if (!root.TryGetValue(key, out OSD node) || node is not OSDMap entries || entries.Count == 0)
            {
                WriteOut("  (none)");
                return;
            }

            foreach (string entryKey in entries.Keys)
            {
                OSD entryValue = entries[entryKey];
                if (entryValue is OSDMap detail)
                {
                    string version = detail.TryGetValue("version_string", out OSD v) ? v.AsString() : "-";
                    string name = detail.TryGetValue("name", out OSD n) ? n.AsString() : entryKey;
                    WriteOut("  - {0} [{1}]", name, version);
                }
                else
                {
                    WriteOut("  - {0}", entryKey);
                }
            }
        }

        private async void HandleJanusListRooms(string module, string[] cmdparms)
        {
            if (_ViewerSession is not null && _ViewerSession.Session is not null && _ViewerSession.AudioBridge is not null)
            {
                var ab = _ViewerSession.AudioBridge;
                var resp = await ab.SendAudioBridgeMsg(new AudioBridgeListRoomsReq());
                if (resp is not null && resp.isSuccess)
                {
                    if (resp.PluginRespData.TryGetValue("list", out OSD list))
                    {
                        MainConsole.Instance.Output("");
                        MainConsole.Instance.Output(
                            "  {0,10} {1,15} {2,5} {3,10} {4,7} {5,7} {6}",
                            "Room", "Description", "Num", "SampleRate", "Spatial", "Recording", "MappedSession");
                        foreach (OSDMap room in list as OSDArray)
                        {
                            MainConsole.Instance.Output(
                                "  {0,10} {1,15} {2,5} {3,10} {4,7} {5,7}",
                                room["room"], room["description"], room["num_participants"],
                                room["sampling_rate"], room["spatial_audio"], room["record"]);
                            var participantResp = await ab.SendAudioBridgeMsg(new AudioBridgeListParticipantsReq(room["room"].AsInteger()));
                            if (participantResp is not null && participantResp.AudioBridgeReturnCode == "participants")
                            {
                                if (participantResp.PluginRespData.TryGetValue("participants", out OSD participants))
                                {
                                    foreach (OSDMap participant in participants as OSDArray)
                                    {
                                        long participantId = participant.TryGetValue("id", out OSD participantIdNode)
                                                ? JanusMessage.OSDToLong(participantIdNode)
                                                : 0L;
                                        string mapping = BuildParticipantMapping(participantId);
                                        MainConsole.Instance.Output("      {0}/{1},muted={2},talking={3},pos={4} {5}",
                                            participantId, participant["display"], participant["muted"],
                                            participant["talking"], participant["spatial_position"],
                                            String.IsNullOrEmpty(mapping) ? "mapped=<none>" : mapping.Substring(2));
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output("No rooms");
                    }
                }
                else
                {
                    MainConsole.Instance.Output("Failed to get room list");
                }
            }
        }

        private async void HandleJanusListSessions(string module, string[] cmdparms)
        {
            if (_ViewerSession is null || _ViewerSession.Session is null)
                return;

            var resp = await _ViewerSession.Session.SendToJanusAdmin(new JanusMessageReq("list_sessions"));
            if (resp is null)
            {
                WriteOut("Failed to get sessions (no response)");
                return;
            }

            if (!resp.isSuccess)
            {
                if (resp.isError)
                {
                    var err = new ErrorResp(resp);
                    WriteOut("Failed to get sessions: {0} ({1})", err.errorReason, err.errorCode);
                }
                else
                {
                    WriteOut("Failed to get sessions: {0}", resp.ReturnCode);
                }
                return;
            }

            OSD sessionsNode = null;
            if (!resp.RawBody.TryGetValue("sessions", out sessionsNode) && resp.dataSection is not null)
            {
                resp.dataSection.TryGetValue("sessions", out sessionsNode);
            }

            if (sessionsNode is not OSDArray sessions)
            {
                WriteOut("No sessions field in admin response");
                return;
            }

            WriteOut("Active Janus sessions: {0}", sessions.Count);
            foreach (OSD session in sessions)
            {
                string janusSessionId = session.AsString();
                if (VoiceViewerSession.TryGetViewerSessionByVSSessionId(janusSessionId, out IVoiceViewerSession viewerSession))
                {
                    WriteOut("  - {0}  viewer_session={1} agent={2} scene={3}",
                            janusSessionId,
                            viewerSession.ViewerSessionID,
                            viewerSession.AgentId,
                            viewerSession.RegionId);
                }
                else
                {
                    WriteOut("  - {0}", janusSessionId);
                }
            }
        }

        private async void HandleJanusRoom(string module, string[] cmdparms)
        {
            if (_ViewerSession is null || _ViewerSession.Session is null || _ViewerSession.AudioBridge is null)
                return;

            if (cmdparms is null || cmdparms.Length < 3 || !int.TryParse(cmdparms[2], out int roomId))
            {
                WriteOut("Usage: janus room <roomId>");
                return;
            }

            var ab = _ViewerSession.AudioBridge;
            var roomsResp = await ab.SendAudioBridgeMsg(new AudioBridgeListRoomsReq());
            if (roomsResp is null || !roomsResp.isSuccess || roomsResp.PluginRespData is null)
            {
                WriteOut("Failed to get room list");
                return;
            }

            if (!roomsResp.PluginRespData.TryGetValue("list", out OSD listNode) || listNode is not OSDArray roomList)
            {
                WriteOut("No rooms available");
                return;
            }

            OSDMap foundRoom = null;
            foreach (OSDMap room in roomList)
            {
                if (room is not null && room.TryGetValue("room", out OSD roomOsd) && roomOsd.AsInteger() == roomId)
                {
                    foundRoom = room;
                    break;
                }
            }

            if (foundRoom is null)
            {
                WriteOut("Room {0} not found", roomId);
                return;
            }

            WriteOut("");
            WriteOut("Room {0}", roomId);
            WriteOut("  Description : {0}", GetMapString(foundRoom, "description"));
            WriteOut("  Participants: {0}", GetMapString(foundRoom, "num_participants"));
            WriteOut("  SampleRate  : {0}", GetMapString(foundRoom, "sampling_rate"));
            WriteOut("  Spatial     : {0}", GetMapString(foundRoom, "spatial_audio"));
            WriteOut("  Recording   : {0}", GetMapString(foundRoom, "record"));

            var participantResp = await ab.SendAudioBridgeMsg(new AudioBridgeListParticipantsReq(roomId));
            if (participantResp is null || participantResp.PluginRespData is null ||
                !participantResp.PluginRespData.TryGetValue("participants", out OSD participantsNode) ||
                participantsNode is not OSDArray participants)
            {
                WriteOut("  Participant list not available");
                return;
            }

            WriteOut("  Participant details:");
            if (participants.Count == 0)
            {
                WriteOut("    (none)");
                return;
            }

            foreach (OSDMap participant in participants)
            {
                long participantId = participant.TryGetValue("id", out OSD participantIdNode)
                        ? JanusMessage.OSDToLong(participantIdNode)
                        : 0L;
                string mapping = BuildParticipantMapping(participantId);
                WriteOut("    - {0}/{1}, muted={2}, talking={3}, pos={4}{5}",
                    participantId,
                    GetMapString(participant, "display"),
                    GetMapString(participant, "muted"),
                    GetMapString(participant, "talking"),
                    GetMapString(participant, "spatial_position"),
                    mapping);
            }
        }

        private static string BuildParticipantMapping(long participantId)
        {
            if (participantId <= 0)
                return "";

            lock (VoiceViewerSession.ViewerSessions)
            {
                foreach (KeyValuePair<string, IVoiceViewerSession> entry in VoiceViewerSession.ViewerSessions)
                {
                    if (entry.Value is JanusViewerSession janusViewerSession && janusViewerSession.ParticipantId == participantId)
                    {
                        return String.Format(", viewer_session={0}, agent={1}, scene={2}",
                                entry.Key,
                                entry.Value.AgentId,
                                entry.Value.RegionId);
                    }
                }
            }

            return "";
        }

        private void WriteOut(string msg, params object[] args)
        {
            // m_log.InfoFormat(msg, args);
            MainConsole.Instance.Output(msg, args);
        }


    }
 }
