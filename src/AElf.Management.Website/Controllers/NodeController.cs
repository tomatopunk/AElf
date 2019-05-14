using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Management.Interfaces;
using AElf.Management.Models;
using AElf.Management.Website.Models;
using Microsoft.AspNetCore.Mvc;

namespace AElf.Management.Website.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NodeController : ControllerBase
    {
        private readonly INodeService _nodeService;

        public NodeController(INodeService nodeService)
        {
            _nodeService = nodeService;
        }

        [HttpGet]
        [Route("statehistory/{chainId}")]
        public async Task<ApiResult<List<NodeStateHistory>>> StateHistory(string chainId)
        {
            var result = await _nodeService.GetHistoryState(chainId);

            return new ApiResult<List<NodeStateHistory>>(result);
        }
    }
}