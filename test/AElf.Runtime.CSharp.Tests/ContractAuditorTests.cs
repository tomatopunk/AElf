using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AElf.Contracts.AssociationAuth;
using AElf.Contracts.Consensus.AEDPoS;
using AElf.Contracts.CrossChain;
using AElf.Contracts.Election;
using AElf.Contracts.Genesis;
using AElf.Contracts.MultiToken;
using AElf.Contracts.ParliamentAuth;
using AElf.Contracts.Profit;
using AElf.Contracts.ReferendumAuth;
using AElf.Contracts.Resource.FeeReceiver;
using AElf.Contracts.TokenConverter;
using AElf.Runtime.CSharp.Validators;
using AElf.Runtime.CSharp.Validators.Method;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using ValidationResult = AElf.Runtime.CSharp.Validators.ValidationResult;

namespace AElf.Runtime.CSharp.Tests
{
    public class ContractAuditorTests : CSharpRuntimeTestBase
    {
        private ContractAuditor _auditor;
        private IEnumerable<ValidationResult> _findings;
        private readonly string _contractDllDir = "../../../contracts/";

        public ContractAuditorTests(ITestOutputHelper testOutputHelper)
        {
            _auditor = new ContractAuditor(null, null);
            
            Should.Throw<InvalidCodeException>(() =>
            {
                try
                {
                    _auditor.Audit(ReadCode(_contractDllDir + typeof(BadContract.BadContract).Module),
                        false);
                }
                catch (InvalidCodeException ex)
                {
                    _findings = ex.Findings;
                    throw ex;
                }
            });
        }
        
        #region Positive Cases
        
        [Fact]
        public void CheckSystemContracts_AllShouldPass()
        {
            var contracts = new[]
            {
                typeof(AssociationAuthContract).Module.ToString(),
                typeof(AEDPoSContract).Module.ToString(),
                typeof(CrossChainContract).Module.ToString(),
                typeof(ElectionContract).Module.ToString(),
                typeof(BasicContractZero).Module.ToString(),
                typeof(TokenContract).Module.ToString(),
                typeof(ParliamentAuthContract).Module.ToString(),
                typeof(ProfitContract).Module.ToString(),
                typeof(ReferendumAuthContract).Module.ToString(),
                typeof(FeeReceiverContract).Module.ToString(),
                typeof(TokenConverterContract).Module.ToString(),
                typeof(TestContract.TestContract).Module.ToString(),
            };

            // Load the DLL's from contracts folder to prevent codecov injection
            foreach (var contract in contracts)
            {
                var contractDllPath = _contractDllDir + contract;
                
                Should.NotThrow(()=>_auditor.Audit(ReadCode(contractDllPath), true));
            }
        }
        
        [Fact]
        public void CheckSystemContracts_Injected_AllShouldPass()
        {
            var injectedContracts = new[]
            {
                typeof(AssociationAuthContract).Assembly.Location,
                typeof(AEDPoSContract).Assembly.Location,
                typeof(CrossChainContract).Assembly.Location,
                typeof(ElectionContract).Assembly.Location,
                typeof(BasicContractZero).Assembly.Location,
                typeof(TokenContract).Assembly.Location,
                typeof(ParliamentAuthContract).Assembly.Location,
                typeof(ProfitContract).Assembly.Location,
                typeof(ReferendumAuthContract).Assembly.Location,
                typeof(FeeReceiverContract).Assembly.Location,
                typeof(TokenConverterContract).Assembly.Location,
                typeof(TestContract.TestContract).Assembly.Location,
            };

            // Load the DLL's from contracts folder to prevent codecov injection
            foreach (var contract in injectedContracts)
            {
                Should.NotThrow(()=>_auditor.Audit(ReadCode(contract), true));
            }
        }
        
        #endregion
        
        #region Negative Cases

        [Fact]
        public void CheckBadContract_ForRandomUsage()
        {
            LookFor(_findings, 
                    "UpdateStateWithRandom", 
                    i => i.Namespace == "System" && i.Type == "Random")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForDateTimeUtcNowUsage()
        {
            LookFor(_findings, 
                    "UpdateStateWithCurrentTime", 
                    i => i.Namespace == "System" && i.Type == "DateTime" && i.Member == "get_UtcNow")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForDateTimeNowUsage()
        {
            LookFor(_findings, 
                    "UpdateStateWithCurrentTime",
                    i => i.Namespace == "System" && i.Type == "DateTime" && i.Member == "get_Now")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForDateTimeTodayUsage()
        {
            LookFor(_findings, 
                    "UpdateStateWithCurrentTime",
                    i => i.Namespace == "System" && i.Type == "DateTime" && i.Member == "get_Today")
                .ShouldNotBeNull();
        }

        [Fact]
        public void CheckBadContract_ForDoubleTypeUsage()
        {
            LookFor(_findings, 
                    "UpdateDoubleState",
                    i => i.Namespace == "System" && i.Type == "Double")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForFloatTypeUsage()
        {
            // http://docs.microsoft.com/en-us/dotnet/api/system.single
            LookFor(_findings, 
                    "UpdateFloatState",
                    i => i.Namespace == "System" && i.Type == "Single") 
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForDiskOpsUsage()
        {
            LookFor(_findings, 
                    "WriteFileToNode",
                    i => i.Namespace == "System.IO")
                .ShouldNotBeNull();
        }

        [Fact]
        public void CheckBadContract_ForStringConstructorUsage()
        {
            LookFor(_findings, 
                "InitLargeStringDynamic",
                i => i.Namespace == "System" && i.Type == "String" && i.Member == ".ctor")
                .ShouldNotBeNull();
        }

        [Fact]
        public void CheckBadContract_ForDeniedMemberUseInNestedClass()
        {
            LookFor(_findings, 
                    "UseDeniedMemberInNestedClass",
                    i => i.Namespace == "System" && i.Type == "DateTime" && i.Member == "get_Now")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForDeniedMemberUseInSeparateClass()
        {
            LookFor(_findings, 
                    "UseDeniedMemberInSeparateClass",
                    i => i.Namespace == "System" && i.Type == "DateTime" && i.Member == "get_Now")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForLargeArrayInitialization()
        {
            _findings.FirstOrDefault(f => f is ArrayValidationResult && f.Info.ReferencingMethod == "InitLargeArray")
                .ShouldNotBeNull();
        }
        
        [Fact]
        public void CheckBadContract_ForFloatOperations()
        {
            _findings.FirstOrDefault(f => f is FloatOpsValidationResult)
                .ShouldNotBeNull();
        }
        
        #endregion

        #region Test Helpers

        private Info LookFor(IEnumerable<ValidationResult>  findings, string referencingMethod, Func<Info, bool> criteria)
        {
            return findings.Select(f => f.Info).FirstOrDefault(i => i != null && i.ReferencingMethod == referencingMethod && criteria(i));
        }

        private byte[] ReadCode(string path)
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException("Contract DLL cannot be found. " + path);
        }
        
        #endregion
    }
}
