using System;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Threading.Tasks;

namespace Lidgren.Network
{
    /// <summary>
    /// UPnP support class.
    /// </summary>
    public class NetUPnP
    {
        private Uri? _serviceUri;
        private string _serviceName = "";
        private TimeSpan _discoveryStartTime;

        public event EventHandler<NetUPnPDiscoveryEventArgs>? ServiceReady;

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
            Status = UPnPStatus.Idle;
        }

        internal void Discover()
        {
            if (Peer.Socket == null)
                throw new InvalidOperationException("The associated peer has no socket.");

            string str =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "ST:upnp:rootdevice\r\n" +
                "MAN:\"ssdp:discover\"\r\n" +
                "MX:3\r\n\r\n";

            byte[] arr = System.Text.Encoding.UTF8.GetBytes(str);

            Peer.LogDebug("Attempting UPnP discovery");
            Peer.Socket.EnableBroadcast = true;
            Peer.RawSend(arr, 0, arr.Length, new IPEndPoint(IPAddress.Broadcast, 1900));
            Peer.Socket.EnableBroadcast = false;

            _discoveryStartTime = NetTime.Now;
            Status = UPnPStatus.Discovering;
        }

        internal void ExtractServiceUri(Uri location)
        {
#if !DEBUG
            try
#endif
            {
                TimeSpan discoveryEndTime = NetTime.Now;

                var desc = new XmlDocument();
                using (var rep = WebRequest.Create(location).GetResponse())
                using (var stream = rep.GetResponseStream())
                    desc.Load(stream);

                var nsMgr = new XmlNamespaceManager(desc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                XmlNode? typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
                if (typen?.Value == null || !typen.Value.Contains("InternetGatewayDevice", StringComparison.Ordinal))
                    return;

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
                    return;

                var controlUri = new Uri(node.Value, UriKind.RelativeOrAbsolute);
                _serviceUri = controlUri.IsAbsoluteUri
                    ? controlUri
                    : new Uri(new Uri(location.GetLeftPart(UriPartial.Authority)), controlUri);

                Status = UPnPStatus.Available;
                Peer.LogDebug("UPnP service ready");
                ServiceReady?.Invoke(this, new NetUPnPDiscoveryEventArgs(_discoveryStartTime, discoveryEndTime));
            }
#if !DEBUG
            catch (Exception exc)
            {
                Status = UPnPStatus.NotAvailable;
                Peer.LogVerbose("Exception ignored trying to parse UPnP XML response: " + exc);
            }
#endif
        }

        private bool IsAvailable()
        {
            switch (Status)
            {
                case UPnPStatus.NotAvailable:
                    return false;

                case UPnPStatus.Available:
                    return true;

                case UPnPStatus.Discovering:
                    if (NetTime.Now > DiscoveryDeadline)
                        Status = UPnPStatus.NotAvailable;
                    return false;
            }
            return false;
        }

        /// <summary>
        /// Add a forwarding rule to the router using UPnP.
        /// </summary>
        public bool ForwardPort(int internalPort, int externalPort, string description)
        {
            if (!IsAvailable() || _serviceUri == null)
                return false;

            if (!NetUtility.GetLocalAddress(out var client, out _))
                return false;

            var invariant = CultureInfo.InvariantCulture;
            try
            {
                var newProtocol = ProtocolType.Udp.ToString().ToUpper(invariant);

                SOAPRequest(
                    _serviceUri,
                    "<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + externalPort.ToString(invariant) + "</NewExternalPort>" +
                    "<NewProtocol>" + newProtocol + "</NewProtocol>" +
                    "<NewInternalPort>" + internalPort.ToString(invariant) + "</NewInternalPort>" +
                    "<NewInternalClient>" + client.ToString() + "</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled>" +
                    "<NewPortMappingDescription>" + description + "</NewPortMappingDescription>" +
                    "<NewLeaseDuration>0</NewLeaseDuration>" +
                    "</u:AddPortMapping>",
                    "AddPortMapping");

                Peer.LogDebug("Sent UPnP port forward request");
            }
            catch (Exception ex)
            {
                Peer.LogWarning("UPnP port forward failed: " + ex.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Delete a forwarding rule from the router using UPnP
        /// </summary>
        public bool DeleteForwardingRule(int port)
        {
            if (!IsAvailable() || _serviceUri == null)
                return false;

            try
            {
                var newProtocol = ProtocolType.Udp.ToString().ToUpper(CultureInfo.InvariantCulture);
                SOAPRequest(_serviceUri,
                    "<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
                    "<NewRemoteHost>" +
                    "</NewRemoteHost>" +
                    "<NewExternalPort>" + port + "</NewExternalPort>" +
                    "<NewProtocol>" + newProtocol + "</NewProtocol>" +
                    "</u:DeletePortMapping>", "DeletePortMapping");
                return true;
            }
            catch (Exception ex)
            {
                Peer.LogWarning("UPnP delete forwarding rule failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Retrieve the extern IP address using UPnP.
        /// </summary>
        public IPAddress? GetExternalIP()
        {
            if (!IsAvailable() || _serviceUri == null)
                return null;

            try
            {
                XmlDocument xdoc = SOAPRequest(
                    _serviceUri,
                    "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
                    "</u:GetExternalIPAddress>",
                    "GetExternalIPAddress");

                var nsMgr = new XmlNamespaceManager(xdoc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                string? ip = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr)?.Value;
                return ip != null ? IPAddress.Parse(ip) : null;
            }
            catch (Exception ex)
            {
                Peer.LogWarning("Failed to get external IP: " + ex.Message);
                return null;
            }
        }

        private XmlDocument SOAPRequest(Uri uri, string soap, string function)
        {
            string reqQuery =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"" +
                " s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                soap +
                "</s:Body>" +
                "</s:Envelope>";

            var req = WebRequest.Create(uri);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add(
                "SOAPACTION",
                "\"urn:schemas-upnp-org:service:" + _serviceName + ":1#" + function + "\"");

            byte[] reqBytes = System.Text.Encoding.UTF8.GetBytes(reqQuery);
            req.ContentLength = reqBytes.Length;

            using (var requestStream = req.GetRequestStream())
                requestStream.Write(reqBytes, 0, reqBytes.Length);

            using var rep = req.GetResponse();
            using var stream = rep.GetResponseStream();
            var resp = new XmlDocument();
            resp.Load(stream);
            return resp;
        }
    }
}
