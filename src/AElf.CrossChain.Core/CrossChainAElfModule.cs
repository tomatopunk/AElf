using System;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Miner.Application;
using AElf.Kernel.SmartContract;
using AElf.Modularity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace AElf.CrossChain
{
    [DependsOn(typeof(SmartContractAElfModule))]
    public class CrossChainAElfModule : AElfModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddTransient<IBlockExtraDataProvider, CrossChainBlockExtraDataProvider>();
            context.Services.AddTransient<ISystemTransactionGenerator, CrossChainIndexingTransactionGenerator>();
            context.Services.AddTransient<IBlockValidationProvider, CrossChainValidationProvider>();
            context.Services.AddTransient<ISmartContractAddressNameProvider, CrossChainSmartContractAddressNameProvider>();
            context.Services.AddSingleton<ICrossChainDataProvider, CrossChainDataProvider>();
            var crossChainConfiguration = context.Services.GetConfiguration().GetSection("CrossChain");
            Configure<CrossChainConfigOption>(option =>
            {
                var parentChainIdString = crossChainConfiguration.GetValue<string>("ParentChainId");
                option.ParentChainId = parentChainIdString.IsNullOrEmpty()
                    ? 0
                    : ChainHelpers.ConvertBase58ToChainId(parentChainIdString);
                option.MaximalCountForIndexingSideChainBlock =
                    crossChainConfiguration.GetValue("MaximalCountForIndexingSideChainBlock",
                        CrossChainConstants.DefaultCountLimitForOnceIndexing);
                option.MaximalCountForIndexingParentChainBlock =
                    crossChainConfiguration.GetValue("MaximalCountForIndexingParentChainBlock",
                        CrossChainConstants.DefaultCountLimitForOnceIndexing);
            });
        }
    }
}