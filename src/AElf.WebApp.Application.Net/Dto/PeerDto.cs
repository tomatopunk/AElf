using System.Collections.Generic;
using AElf.OS.Network;

namespace AElf.WebApp.Application.Net.Dto
{
    public class PeerDto
    {
        public string IpAddress { get; set; }
        
        public int ProtocolVersion { get; set; }
        
        public long ConnectionTime { get; set; }
        
        public bool Inbound { get; set; }
        
        public long StartHeight { get; set; }
        
        public List<RequestMetric> RequestMetrics { get; set; }
    }
}