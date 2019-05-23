﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Management.Database;
using AElf.Management.Interfaces;
using AElf.Management.Models;
using AElf.Management.Request;
using Microsoft.Extensions.Options;

namespace AElf.Management.Services
{
    public class NodeService : INodeService
    {
        private readonly ManagementOptions _managementOptions;
        private readonly IInfluxDatabase _influxDatabase;

        public NodeService(IOptionsSnapshot<ManagementOptions> options, IInfluxDatabase influxDatabase)
        {
            _managementOptions = options.Value;
            _influxDatabase = influxDatabase;
        }

        public async Task<List<NodeStateHistory>> GetHistoryStateAsync(string chainId)
        {
            var result = new List<NodeStateHistory>();
            var record = await _influxDatabase.QueryAsync(chainId, "select * from node_state");
            foreach (var item in record.First().Values)
            {
                result.Add(new NodeStateHistory
                {
                    Time = Convert.ToDateTime(item[0]),
                    IsAlive = Convert.ToBoolean(item[1]),
                    IsForked = Convert.ToBoolean(item[2])
                });
            }

            return result;
        }

        public async Task RecordBlockInfoAsync(string chainId)
        {
            long currentHeight;
            var currentRecord = await _influxDatabase.QueryAsync(chainId, "select last(height) from block_info");
            if (currentRecord.Count == 0)
            {
                currentHeight = await GetCurrentChainHeight(chainId);
            }
            else
            {
                var record = currentRecord.First().Values.First();
                var time = Convert.ToDateTime(record[0]);

                if (time < DateTime.Now.AddHours(-1))
                {
                    currentHeight = await GetCurrentChainHeight(chainId);
                }
                else
                {
                    currentHeight = Convert.ToInt64(record[1]) + 1;
                }
            }

            var blockInfo = await GetBlockInfo(chainId, currentHeight);
            while (blockInfo != null && blockInfo.Body != null && blockInfo.Header != null)
            {
                var fields = new Dictionary<string, object>
                    {{"height", currentHeight}, {"tx_count", blockInfo.Body.TransactionsCount}};
                await _influxDatabase.WriteAsync(chainId, "block_info", fields, null, blockInfo.Header.Time);

                Thread.Sleep(1000);

                currentHeight++;
                blockInfo = await GetBlockInfo(chainId, currentHeight);
            }
        }

        public async Task RecordGetCurrentChainStatusAsync(string chainId)
        {
            var count = await GetCurrentChainStatus(chainId);

            var fields = new Dictionary<string, object> {{"LastIrrever", count.LastIrreversibleBlockHeight},{"Longest", count.LongestChainHeight},{"Best", count.BestChainHeight}};
            await _influxDatabase.WriteAsync(chainId, "block_status", fields, null, DateTime.UtcNow);
        }

        public async Task RecordTaskQueueStatusAsync(string chainId)
        {
            var taskQueues = await GetTaskQueueStateAsync(chainId);
            var fields = new Dictionary<string, object>();
            foreach (var taskQueue  in taskQueues)
            {
                fields.Add(taskQueue.Name, taskQueue.Size);
            }
            await _influxDatabase.WriteAsync(chainId, "task_queue_status", fields, null, DateTime.UtcNow);
        }

        private async Task<List<TaskQueueStatus>> GetTaskQueueStateAsync(string chainId)
        {
            var url = $"{_managementOptions.ServiceUrls[chainId].RpcAddress}/api/blockChain/taskQueueStatus";
            var taskQueueStatus = await HttpRequestHelper.Get<List<TaskQueueStatus>>(url);
            return taskQueueStatus;
        }

        private async Task<BlockInfoResult> GetBlockInfo(string chainId, long height)
        {
            var url = $"{_managementOptions.ServiceUrls[chainId].RpcAddress}/api/blockChain/blockByHeight" +
                      $"?blockHeight={height}&includeTransactions=false";
            var blockInfo = await HttpRequestHelper.Get<BlockInfoResult>(url);
            return blockInfo;
        }

        private async Task<long> GetCurrentChainHeight(string chainId)
        {
            var url = $"{_managementOptions.ServiceUrls[chainId].RpcAddress}/api/blockChain/blockHeight";
            return await HttpRequestHelper.Get<int>(url);;
        } 
         
        private async Task<ChainStatusResult> GetCurrentChainStatus(string chainId)
        {
            var url = $"{_managementOptions.ServiceUrls[chainId].RpcAddress}/api/blockChain/chainStatus";
            return await HttpRequestHelper.Get<ChainStatusResult>(url);;
        }
    }
}