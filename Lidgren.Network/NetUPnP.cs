using System;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// UPnP helper allowing port forwarding and getting external IP.
    /// </summary>
    public class NetUPnP
    {
        private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private Uri? _serviceUri;
        private string _serviceName = "";
        private TimeSpan _discoveryStartTime;

        public event EventHandler<NetUPnPDiscoveryEventArgs>? StatusChanged;

        /// <summary>
        /// Gets the associated <see cref="NetPeer"/>.
        /// </summary>
        public NetPeer Peer { get; }

        public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Gets the status of the UPnP capabilities for <see cref="Peer"/>.
        /// </summary>
        public UPnPStatus Status { get; private set; }

        public TimeSpan DiscoveryDeadline => _discoveryStartTime + DiscoveryTimeout;

        /// <summary>
        /// Constructs the <see cref="NetUPnP"/> helper.
        /// </summary>
        public NetUPnP(NetPeer peer)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        }

        public void Discover()
        {
            if (Peer.Socket == null)
                throw new InvalidOperationException("The associated peer has no socket.");

            string str =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "ST:upnp:rootdevice\r\n" +
                "MAN:\"ssdp:discover\"\r\n" +
                "MX:3\r\n\r\n";

            byte[] arr = _encoding.GetBytes(str);

            _discoveryStartTime = NetTime.Now;
            Status = UPnPStatus.Discovering;

            Peer.Socket.EnableBroadcast = true;
            Peer.RawSend(arr, new NetAddress(IPAddress.Broadcast, 1900));
            Peer.Socket.EnableBroadcast = false;
        }

        public Task<NetUPnPDiscoveryEventArgs> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<NetUPnPDiscoveryEventArgs> source = new();

            CancellationTokenRegistration registration = cancellationToken.Register(
                () => source.TrySetCanceled(cancellationToken));

            StatusChanged += (s, e) =>
            {
                if (e.Status != UPnPStatus.Discovering)
                {
                    source.TrySetResult(e);
                    registration.Dispose();
                }
            };
            Discover();

            return source.Task;
        }

        internal void Invalidate(TimeSpan endTime)
        {
            _discoveryStartTime = default;
            _serviceName = "";
            _serviceUri = null;
            Status = UPnPStatus.NotAvailable;
            StatusChanged?.Invoke(this, new NetUPnPDiscoveryEventArgs(this, Status, _discoveryStartTime, endTime));
        }

        internal void ExtractServiceUri(Uri location)
        {
            TimeSpan discoveryEndTime = NetTime.Now;
            try
            {
                var desc = new XmlDocument();
                using (WebResponse rep = WebRequest.Create(location).GetResponse())
                using (Stream stream = rep.GetResponseStream())
                    desc.Load(stream);

                var nsMgr = new XmlNamespaceManager(desc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                XmlNode? typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
                if (typen?.Value == null || !typen.Value.Contains("InternetGatewayDevice", StringComparison.Ordinal))
                {
                    Invalidate(discoveryEndTime);
                    return;
                }

                _serviceName = "WANIPConnection";

                XmlNode? node = desc.SelectSingleNode(
                    "//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" +
                    _serviceName + ":1\"]/tns:controlURL/text()", nsMgr);

                if (node == null)
                {
                    //try another service name
                    _serviceName = "WANPPPConnection";

                    node = desc.SelectSingleNode(
                        "//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" +
                        _serviceName + ":1\"]/tns:controlURL/text()", nsMgr);
                }

                if (node?.Value == null)
                {
                    Invalidate(discoveryEndTime);
                    return;
                }

                Uri controlUri = new(node.Value, UriKind.RelativeOrAbsolute);
                _serviceUri = controlUri.IsAbsoluteUri
                    ? controlUri
                    : new Uri(new Uri(location.GetLeftPart(UriPartial.Authority)), controlUri);

                Status = UPnPStatus.Available;
                StatusChanged?.Invoke(this, new NetUPnPDiscoveryEventArgs(this, Status, _discoveryStartTime, discoveryEndTime));
            }
            catch
            {
                Invalidate(discoveryEndTime);
                throw;
            }
        }

        public bool IsAvailable()
        {
            switch (Status)
            {
                case UPnPStatus.Available:
                    return true;

                case UPnPStatus.Discovering:
                    TimeSpan now = NetTime.Now;
                    if (now > DiscoveryDeadline)
                    {
                        Invalidate(now);
                    }
                    return false;

                case UPnPStatus.NotAvailable:
                default:
                    return false;
            }
        }

        /// <summary>
        /// Add a forwarding rule to the router using UPnP.
        /// </summary>
        public async Task<bool> ForwardPortAsync(
            int internalPort, int externalPort, string description, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable() || _serviceUri == null)
                return false;

            if (!NetUtility.GetLocalAddress(out IPAddress? client, out _))
                return false;

            try
            {
                var culture = CultureInfo.InvariantCulture;
                var newProtocol = ProtocolType.Udp.ToString().ToUpper(culture);

                StringBuilder soap = new();
                soap.Append("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:")
                    .Append(_serviceName).Append(":1\">");
                soap.Append("<NewRemoteHost></NewRemoteHost>");
                soap.Append("<NewExternalPort>").Append(externalPort.ToString(culture)).Append("</NewExternalPort>");
                soap.Append("<NewProtocol>").Append(newProtocol).Append("</NewProtocol>");
                soap.Append("<NewInternalPort>").Append(internalPort.ToString(culture)).Append("</NewInternalPort>");
                soap.Append("<NewInternalClient>").Append(client.ToString()).Append("</NewInternalClient>");
                soap.Append("<NewEnabled>1</NewEnabled>");
                soap.Append("<NewPortMappingDescription>").Append(description).Append("</NewPortMappingDescription>");
                soap.Append("<NewLeaseDuration>0</NewLeaseDuration>");
                soap.Append("</u:AddPortMapping>");

                await SOAPRequestAsync(_serviceUri, soap, "AddPortMapping", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Peer.LogWarning(new NetLogMessage(NetLogCode.UPnPPortForwardFailed, ex));
                return false;
            }
            return true;
        }

        public bool ForwardPort(
            int internalPort, int externalPort, string description, CancellationToken cancellationToken = default)
        {
            return ForwardPortAsync(internalPort, externalPort, description, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Delete a forwarding rule from the router using UPnP
        /// </summary>
        public async Task<bool> DeleteForwardingRuleAsync(int port, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable() || _serviceUri == null)
                return false;

            try
            {
                var newProtocol = ProtocolType.Udp.ToString().ToUpper(CultureInfo.InvariantCulture);

                StringBuilder soap = new();
                soap.Append("<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:")
                    .Append(_serviceName).Append(":1\">");
                soap.Append("<NewRemoteHost></NewRemoteHost>");
                soap.Append("<NewExternalPort>").Append(port).Append("</NewExternalPort>");
                soap.Append("<NewProtocol>").Append(newProtocol).Append("</NewProtocol>");
                soap.Append("</u:DeletePortMapping>");

                await SOAPRequestAsync(_serviceUri, soap, "DeletePortMapping", cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Peer.LogWarning(new NetLogMessage(NetLogCode.UPnPPortDeleteFailed, ex));
                return false;
            }
        }

        public bool DeleteForwardingRule(int port, CancellationToken cancellationToken = default)
        {
            return DeleteForwardingRuleAsync(port, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Retrieve the extern IP address using UPnP.
        /// </summary>
        public async Task<IPAddress?> GetExternalIPAsync(CancellationToken cancellationToken = default)
        {
            if (!IsAvailable() || _serviceUri == null)
                return null;

            try
            {
                StringBuilder soap = new();
                soap.Append("<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:");
                soap.Append(_serviceName);
                soap.Append(":1\"></u:GetExternalIPAddress>");

                XmlDocument xdoc = await SOAPRequestAsync(_serviceUri, soap, "GetExternalIPAddress", cancellationToken)
                    .ConfigureAwait(false);

                var nsMgr = new XmlNamespaceManager(xdoc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                string? ip = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr)?.Value;
                return ip != null ? IPAddress.Parse(ip) : null;
            }
            catch (Exception ex)
            {
                Peer.LogWarning(new NetLogMessage(NetLogCode.UPnPExternalIPFailed, ex));
                return null;
            }
        }

        public IPAddress? GetExternalIP(CancellationToken cancellationToken = default)
        {
            return GetExternalIPAsync(cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task<XmlDocument> SOAPRequestAsync(
            Uri uri, StringBuilder soapBody, string soapAction, CancellationToken cancellationToken)
        {
            soapBody.Insert(0,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"" +
                " s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>");
            soapBody.Append(
                "</s:Body>" +
                "</s:Envelope>");

            var req = WebRequest.Create(uri);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add(
                "SOAPACTION",
                "\"urn:schemas-upnp-org:service:" + _serviceName + ":1#" + soapAction + "\"");

            int contentLength = 0;
            foreach (ReadOnlyMemory<char> chunk in soapBody.GetChunks())
            {
                contentLength += _encoding.GetByteCount(chunk.Span);
            }
            req.ContentLength = contentLength;

            using (Stream requestStream = await req.GetRequestStreamAsync().ConfigureAwait(false))
            using (StreamWriter writer = new(requestStream, _encoding))
            {
                foreach (ReadOnlyMemory<char> chunk in soapBody.GetChunks())
                {
                    await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
            }

            using WebResponse rep = await req.GetResponseAsync().ConfigureAwait(false);
            using Stream stream = rep.GetResponseStream();
            var resp = new XmlDocument();
            resp.Load(stream);
            return resp;
        }
    }
}
