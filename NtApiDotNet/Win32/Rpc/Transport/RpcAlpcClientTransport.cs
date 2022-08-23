﻿//  Copyright 2019 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using NtApiDotNet.Ndr.Marshal;
using System;
using System.Collections.Generic;

namespace NtApiDotNet.Win32.Rpc.Transport
{
    /// <summary>
    /// RPC client transport over ALPC.
    /// </summary>
    public class RpcAlpcClientTransport : IRpcClientTransport
    {
        #region Private Members
        private NtAlpcClient _client;
        private readonly SecurityQualityOfService _sqos;

        private static AlpcPortAttributes CreatePortAttributes(SecurityQualityOfService sqos)
        {
            AlpcPortAttributeFlags flags = AlpcPortAttributeFlags.AllowDupObject | AlpcPortAttributeFlags.AllowImpersonation | AlpcPortAttributeFlags.WaitablePort;
            if (!NtObjectUtils.IsWindows81OrLess)
            {
                flags |= AlpcPortAttributeFlags.AllowMultiHandleAttribute;
            }

            return new AlpcPortAttributes()
            {
                DupObjectTypes = AlpcHandleObjectType.AllObjects,
                MemoryBandwidth = new IntPtr(0),
                Flags = flags,
                MaxMessageLength = new IntPtr(0x1000),
                MaxPoolUsage = new IntPtr(-1),
                MaxSectionSize = new IntPtr(-1),
                MaxViewSize = new IntPtr(-1),
                MaxTotalSectionSize = new IntPtr(-1),
                SecurityQos = sqos?.ToStruct() ?? new SecurityQualityOfServiceStruct(SecurityImpersonationLevel.Impersonation, SecurityContextTrackingMode.Static, false)
            };
        }

        private static NtAlpcClient ConnectPort(string path, SecurityQualityOfService sqos, NtWaitTimeout timeout)
        {
            AlpcReceiveMessageAttributes in_attr = new AlpcReceiveMessageAttributes();
            return NtAlpcClient.Connect(path, null,
                CreatePortAttributes(sqos), AlpcMessageFlags.SyncRequest, null, null, null, in_attr, timeout);
        }

        private static void CheckForFault(SafeHGlobalBuffer buffer, LRPC_MESSAGE_TYPE message_type)
        {
            var header = buffer.Read<LRPC_HEADER>(0);
            if (header.MessageType != LRPC_MESSAGE_TYPE.lmtFault && header.MessageType != message_type)
            {
                throw new RpcTransportException($"Invalid response message type {header.MessageType}");
            }

            if (header.MessageType == LRPC_MESSAGE_TYPE.lmtFault)
            {
                var fault = buffer.GetStructAtOffset<LRPC_FAULT_MESSAGE>(0);
                throw new RpcFaultException(fault);
            }
        }

        private void BindInterface(Guid interface_id, Version interface_version)
        {
            var bind_msg = new AlpcMessageType<LRPC_BIND_MESSAGE>(new LRPC_BIND_MESSAGE(interface_id, interface_version));
            RpcUtils.DumpBuffer(true, "ALPC BindInterface Send", bind_msg); 
            var recv_msg = new AlpcMessageRaw(0x1000);

            using (var recv_attr = new AlpcReceiveMessageAttributes())
            {
                _client.SendReceive(AlpcMessageFlags.SyncRequest, bind_msg, null, recv_msg, recv_attr, NtWaitTimeout.Infinite);
                RpcUtils.DumpBuffer(true, "ALPC BindInterface Receive", recv_msg);
                using (var buffer = recv_msg.Data.ToBuffer())
                {
                    CheckForFault(buffer, LRPC_MESSAGE_TYPE.lmtBind);
                    var value = buffer.Read<LRPC_BIND_MESSAGE>(0);
                    if (value.RpcStatus != 0)
                    {
                        throw new NtException(NtObjectUtils.MapDosErrorToStatus(value.RpcStatus));
                    }
                }
            }
        }

        private RpcClientResponse HandleLargeResponse(AlpcMessageRaw message, SafeStructureInOutBuffer<LRPC_LARGE_RESPONSE_MESSAGE> response, AlpcReceiveMessageAttributes attributes)
        {
            if (!attributes.HasValidAttribute(AlpcMessageAttributeFlags.View))
            {
                throw new RpcTransportException("Large response received but no data view available");
            }

            return new RpcClientResponse(attributes.DataView.ReadBytes(response.Result.LargeDataSize), attributes.Handles);
        }

        private RpcClientResponse HandleImmediateResponse(AlpcMessageRaw message, SafeStructureInOutBuffer<LRPC_IMMEDIATE_RESPONSE_MESSAGE> response, AlpcReceiveMessageAttributes attributes, int data_length)
        {
            return new RpcClientResponse(response.Data.ToArray(), attributes.Handles);
        }

        private RpcClientResponse HandleResponse(AlpcMessageRaw message, AlpcReceiveMessageAttributes attributes, int call_id)
        {
            using (var buffer = message.Data.ToBuffer())
            {
                CheckForFault(buffer, LRPC_MESSAGE_TYPE.lmtResponse);
                // Get data as safe buffer.
                var response = buffer.Read<LRPC_IMMEDIATE_RESPONSE_MESSAGE>(0);
                if (response.CallId != call_id)
                {
                    throw new RpcTransportException("Mismatched Call ID");
                }

                if ((response.Flags & LRPC_RESPONSE_MESSAGE_FLAGS.ViewPresent) == LRPC_RESPONSE_MESSAGE_FLAGS.ViewPresent)
                {
                    return HandleLargeResponse(message, buffer.GetStructAtOffset<LRPC_LARGE_RESPONSE_MESSAGE>(0), attributes);
                }
                return HandleImmediateResponse(message, buffer.GetStructAtOffset<LRPC_IMMEDIATE_RESPONSE_MESSAGE>(0), attributes, message.DataLength);
            }
        }

        private void ClearAttributes(AlpcMessage msg, AlpcReceiveMessageAttributes attributes)
        {
            AlpcMessageAttributeFlags flags = attributes.ValidAttributes & (AlpcMessageAttributeFlags.View | AlpcMessageAttributeFlags.Handle);
            if (!msg.ContinuationRequired || flags == 0)
            {
                return;
            }

            _client.Send(AlpcMessageFlags.None, msg, attributes.ToContinuationAttributes(flags), NtWaitTimeout.Infinite);
        }

        private RpcClientResponse SendAndReceiveLarge(int proc_num, Guid objuuid, byte[] ndr_buffer, IReadOnlyCollection<NtObject> handles)
        {
            LRPC_LARGE_REQUEST_MESSAGE req_msg = new LRPC_LARGE_REQUEST_MESSAGE()
            {
                Header = new LRPC_HEADER(LRPC_MESSAGE_TYPE.lmtRequest),
                BindingId = 0,
                CallId = CallId++,
                ProcNum = proc_num,
                LargeDataSize = ndr_buffer.Length,
                Flags = LRPC_REQUEST_MESSAGE_FLAGS.ViewPresent
            };

            if (objuuid != Guid.Empty)
            {
                req_msg.ObjectUuid = objuuid;
                req_msg.Flags |= LRPC_REQUEST_MESSAGE_FLAGS.ObjectUuid;
            }

            var send_msg = new AlpcMessageType<LRPC_LARGE_REQUEST_MESSAGE>(req_msg);
            var recv_msg = new AlpcMessageRaw(0x1000);
            var send_attr = new AlpcSendMessageAttributes();

            if (handles.Count > 0)
            {
                send_attr.AddHandles(handles);
            }

            using (var port_section = _client.CreatePortSection(AlpcCreatePortSectionFlags.Secure, ndr_buffer.Length))
            {
                using (var data_view = port_section.CreateSectionView(AlpcDataViewAttrFlags.Secure | AlpcDataViewAttrFlags.AutoRelease, ndr_buffer.Length))
                {
                    data_view.WriteBytes(ndr_buffer);
                    send_attr.Add(data_view.ToMessageAttribute());
                    using (var recv_attr = new AlpcReceiveMessageAttributes())
                    {
                        RpcUtils.DumpBuffer(true, "ALPC Request Large", send_msg);
                        _client.SendReceive(AlpcMessageFlags.SyncRequest, send_msg, send_attr, recv_msg, recv_attr, NtWaitTimeout.Infinite);
                        RpcUtils.DumpBuffer(true, "ALPC Response Large", recv_msg);
                        RpcClientResponse response = HandleResponse(recv_msg, recv_attr, req_msg.CallId);
                        ClearAttributes(recv_msg, recv_attr);
                        return response;
                    }
                }
            }
        }

        private RpcClientResponse SendAndReceiveImmediate(int proc_num, Guid objuuid, byte[] ndr_buffer, IReadOnlyCollection<NtObject> handles)
        {
            LRPC_IMMEDIATE_REQUEST_MESSAGE req_msg = new LRPC_IMMEDIATE_REQUEST_MESSAGE()
            {
                Header = new LRPC_HEADER(LRPC_MESSAGE_TYPE.lmtRequest),
                BindingId = 0,
                CallId = CallId++,
                ProcNum = proc_num,
            };

            if (objuuid != Guid.Empty)
            {
                req_msg.ObjectUuid = objuuid;
                req_msg.Flags |= LRPC_REQUEST_MESSAGE_FLAGS.ObjectUuid;
            }

            AlpcMessageType<LRPC_IMMEDIATE_REQUEST_MESSAGE> send_msg = new AlpcMessageType<LRPC_IMMEDIATE_REQUEST_MESSAGE>(req_msg, ndr_buffer);
            AlpcMessageRaw resp_msg = new AlpcMessageRaw(0x1000);
            AlpcSendMessageAttributes send_attr = new AlpcSendMessageAttributes();

            if (handles.Count > 0)
            {
                send_attr.AddHandles(handles);
            }

            using (AlpcReceiveMessageAttributes recv_attr = new AlpcReceiveMessageAttributes())
            {
                RpcUtils.DumpBuffer(true, "ALPC Request Immediate", send_msg);
                _client.SendReceive(AlpcMessageFlags.SyncRequest, send_msg, send_attr, resp_msg, recv_attr, NtWaitTimeout.Infinite);
                RpcUtils.DumpBuffer(true, "ALPC Response Immediate", resp_msg);
                RpcClientResponse response = HandleResponse(resp_msg, recv_attr, req_msg.CallId);
                ClearAttributes(resp_msg, recv_attr);
                return response;
            }
        }

        #endregion

        #region Constructors
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="path">The path to connect. The format depends on the transport.</param>
        /// <param name="security_quality_of_service">The security quality of service for the connection.</param>
        public RpcAlpcClientTransport(string path, SecurityQualityOfService security_quality_of_service) 
            : this(path, security_quality_of_service, NtWaitTimeout.FromSeconds(5))
        {
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="path">The path to connect. The format depends on the transport.</param>
        /// <param name="security_quality_of_service">The security quality of service for the connection.</param>
        /// <param name="timeout">Timeout for connection.</param>
        public RpcAlpcClientTransport(string path, SecurityQualityOfService security_quality_of_service, NtWaitTimeout timeout)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Must specify a path to connect to");
            }

            if (timeout is null)
            {
                throw new ArgumentNullException(nameof(timeout));
            }

            if (!path.StartsWith(@"\"))
            {
                path = $@"\RPC Control\{path}";
            }

            _client = ConnectPort(path, security_quality_of_service, timeout);
            _sqos = security_quality_of_service;
            Endpoint = path;
        }

        #endregion

        #region Public Methods
        /// <summary>
        /// Bind the RPC transport to an interface.
        /// </summary>
        /// <param name="interface_id">The interface ID to bind to.</param>
        /// <param name="interface_version">The interface version to bind to.</param>
        /// <param name="transfer_syntax_id">The transfer syntax to use.</param>
        /// <param name="transfer_syntax_version">The transfer syntax version to use.</param>
        public void Bind(Guid interface_id, Version interface_version, Guid transfer_syntax_id, Version transfer_syntax_version)
        {
            if (transfer_syntax_id != Ndr.NdrNativeUtils.DCE_TransferSyntax || transfer_syntax_version != new Version(2, 0))
            {
                throw new ArgumentException("Only supports DCE transfer syntax");
            }

            CallId = 1;
            BindInterface(interface_id, interface_version);
        }

        /// <summary>
        /// Send and receive an RPC message.
        /// </summary>
        /// <param name="proc_num">The procedure number.</param>
        /// <param name="objuuid">The object UUID for the call.</param>
        /// <param name="data_representation">NDR data representation.</param>
        /// <param name="ndr_buffer">Marshal NDR buffer for the call.</param>
        /// <param name="handles">List of handles marshaled into the buffer.</param>
        /// <returns>Client response from the send.</returns>
        public RpcClientResponse SendReceive(int proc_num, Guid objuuid, NdrDataRepresentation data_representation,
            byte[] ndr_buffer, IReadOnlyCollection<NtObject> handles)
        {
            if (ndr_buffer.Length > 0xF00)
            {
                return SendAndReceiveLarge(proc_num, objuuid, ndr_buffer, handles);
            }
            return SendAndReceiveImmediate(proc_num, objuuid, ndr_buffer, handles);
        }

        /// <summary>
        /// Dispose of the client.
        /// </summary>
        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }

        /// <summary>
        /// Disconnect the client.
        /// </summary>
        public void Disconnect()
        {
            Dispose();
        }

        /// <summary>
        /// Add and authenticate a new security context.
        /// </summary>
        /// <param name="transport_security">The transport security for the context.</param>
        /// <returns>The created security context.</returns>
        public RpcTransportSecurityContext AddSecurityContext(RpcTransportSecurity transport_security)
        {
            throw new InvalidOperationException("Transport doesn't support multiple security context.");
        }

        #endregion

        #region Public Properties
        /// <summary>
        /// Get whether the client is connected or not.
        /// </summary>
        public bool Connected => _client != null && !_client.Handle.IsInvalid;

        /// <summary>
        /// Get the ALPC port path that we connected to.
        /// </summary>
        public string Endpoint { get; private set; }

        /// <summary>
        /// Get the current Call ID.
        /// </summary>
        public int CallId { get; private set; }

        /// <summary>
        /// Get the transport protocol sequence.
        /// </summary>
        public string ProtocolSequence => "ncalrpc";

        /// <summary>
        /// Get information about the local server process, if known.
        /// </summary>
        public RpcServerProcessInformation ServerProcess
        {
            get
            {
                if (!Connected)
                    throw new InvalidOperationException("ALPC transport is not connected.");
                return new RpcServerProcessInformation(_client.ServerProcessId, _client.ServerSessionId);
            }
        }

        /// <summary>
        /// Get whether the client has been authenticated.
        /// </summary>
        public bool Authenticated => Connected;

        /// <summary>
        /// Get the transports authentication type.
        /// </summary>
        public RpcAuthenticationType AuthenticationType => Authenticated ? RpcAuthenticationType.WinNT : RpcAuthenticationType.None;

        /// <summary>
        /// Get the transports authentication level.
        /// </summary>
        public RpcAuthenticationLevel AuthenticationLevel => Authenticated ? RpcAuthenticationLevel.PacketPrivacy : RpcAuthenticationLevel.None;

        /// <summary>
        /// Indicates if this connection supported multiple security context.
        /// </summary>
        public bool SupportsMultipleSecurityContexts => false;

        /// <summary>
        /// Get the list of negotiated security context.
        /// </summary>
        public IReadOnlyList<RpcTransportSecurityContext> SecurityContext => 
            new List<RpcTransportSecurityContext>() { CurrentSecurityContext }.AsReadOnly();

        /// <summary>
        /// Get or set the current security context.
        /// </summary>
        public RpcTransportSecurityContext CurrentSecurityContext {
            get => new RpcTransportSecurityContext(this, new RpcTransportSecurity(_sqos)
            {
                AuthenticationType = AuthenticationType,
                AuthenticationLevel = AuthenticationLevel
            }, 0);
            set => throw new InvalidOperationException("Transport doesn't support multiple security context."); }

        /// <summary>
        /// Get whether the transport supports synchronous pipes.
        /// </summary>
        public bool SupportsSynchronousPipes => false;

        #endregion
    }
}
