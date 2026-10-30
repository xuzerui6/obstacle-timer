using System.Net;
using System.Net.Sockets;
internal static class TestPorts
{
 public static int Pair(){
  // Do not briefly bind/release the chosen port: local proxy/security probes can
  // connect during that interval and leave it unavailable to Kestrel on Windows.
  var ip=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
  var used=ip.GetActiveTcpListeners().Select(p=>p.Port).Concat(ip.GetActiveTcpConnections().Select(p=>p.LocalEndPoint.Port)).ToHashSet();
  for(int attempt=0;attempt<1000;attempt++){int port=Random.Shared.Next(20000,60000);if(!used.Contains(port)&&!used.Contains(port+1))return port;}
  throw new Exception("No free adjacent test ports");
 }
}
