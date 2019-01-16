using System.Collections.Generic;
using AElf.Kernel;

namespace AElf.Node.EventMessages
{
    public sealed class BlockReceived
    {
        public IBlock Block { get; }
        public List<IBlock> Branch { get; }
        
        public BlockReceived(IBlock block)
        {
            Block = block;
        }

        public BlockReceived(List<IBlock> branch)
        {
            Branch = branch;
        }
    }
}