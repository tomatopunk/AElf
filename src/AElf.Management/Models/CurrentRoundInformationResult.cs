using System;
using System.Collections.Generic;

namespace AElf.Management.Models
{
    
    public class CurrentRoundInformationResult
    {
        public Dictionary<string, CurrentRoundInformationDetail> RealTimeMinerInformation { get; set; }
        
        public long RoundId { get; set; }
    }
    
    public class CurrentRoundInformationDetail
    {
        public DateTime ExpectedMiningTime { get; set; }

        public List<DateTime> ActualMiningTimes { get; set; }
    }
}