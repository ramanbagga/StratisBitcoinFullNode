﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Protocol;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Base.Deployments;
using Stratis.Bitcoin.BlockPulling;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Configuration.Logging;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Features.Consensus.CoinViews;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Stratis.Bitcoin.Features.Consensus
{
    public class ConsensusFeature : FullNodeFeature
    {
        /// <summary>Factory for creating and also possibly starting application defined tasks inside async loop.</summary>
        private readonly IAsyncLoopFactory asyncLoopFactory;

        /// <summary>The async loop we need to wait upon before we can shut down this feature.</summary>
        private IAsyncLoop asyncLoop;

        private readonly DBreezeCoinView dBreezeCoinView;
        private readonly Network network;
        private readonly ConcurrentChain chain;
        private readonly PowConsensusValidator consensusValidator;
        private readonly LookaheadBlockPuller blockPuller;
        private readonly CoinView coinView;
        private readonly ChainState chainState;
        private readonly IConnectionManager connectionManager;
        private readonly INodeLifetime nodeLifetime;
        private readonly Signals.Signals signals;
        private readonly ConsensusLoop consensusLoop;
        private readonly NodeSettings nodeSettings;
        private readonly NodeDeployments nodeDeployments;
        private readonly StakeChainStore stakeChain;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly IDateTimeProvider dateTimeProvider;
        private readonly ConsensusManager consensusManager;
        private readonly CacheSettings cacheSettings;

        public ConsensusFeature(
            IAsyncLoopFactory asyncLoopFactory,
            DBreezeCoinView dBreezeCoinView,
            Network network,
            PowConsensusValidator consensusValidator,
            ConcurrentChain chain,
            LookaheadBlockPuller blockPuller,
            CoinView coinView,
            ChainState chainState,
            IConnectionManager connectionManager,
            INodeLifetime nodeLifetime,
            Signals.Signals signals,
            ConsensusLoop consensusLoop,
            NodeSettings nodeSettings,
            NodeDeployments nodeDeployments,
            ILoggerFactory loggerFactory,
            IDateTimeProvider dateTimeProvider,
            ConsensusManager consensusManager,
            CacheSettings cacheSettings,
            StakeChainStore stakeChain = null)
        {
            this.asyncLoopFactory = asyncLoopFactory;
            this.dBreezeCoinView = dBreezeCoinView;
            this.consensusValidator = consensusValidator;
            this.chain = chain;
            this.blockPuller = blockPuller;
            this.coinView = coinView;
            this.chainState = chainState;
            this.connectionManager = connectionManager;
            this.nodeLifetime = nodeLifetime;
            this.signals = signals;
            this.network = network;
            this.consensusLoop = consensusLoop;
            this.nodeSettings = nodeSettings;
            this.nodeDeployments = nodeDeployments;
            this.cacheSettings = cacheSettings;
            this.stakeChain = stakeChain;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.loggerFactory = loggerFactory;
            this.dateTimeProvider = dateTimeProvider;
            this.consensusManager = consensusManager;
        }

        public override void Start()
        {
            this.dBreezeCoinView.Initialize().GetAwaiter().GetResult();
            var cache = this.coinView as CachedCoinView;
            if (cache != null)
            {
                cache.MaxItems = this.cacheSettings.MaxItems;
            }
            this.consensusLoop.Initialize();

            this.chainState.HighestValidatedPoW = this.consensusLoop.Tip;
            this.connectionManager.Parameters.TemplateBehaviors.Add(new BlockPullerBehavior(this.blockPuller, this.loggerFactory));

            var flags = this.nodeDeployments.GetFlags(this.consensusLoop.Tip);
            if (flags.ScriptFlags.HasFlag(ScriptVerify.Witness))
                this.connectionManager.AddDiscoveredNodesRequirement(NodeServices.NODE_WITNESS);

            this.stakeChain?.Load().GetAwaiter().GetResult();

            this.asyncLoop = this.asyncLoopFactory.Run($"Consensus Loop", async token =>
            {
                await this.RunLoop(this.nodeLifetime.ApplicationStopping);
            }, this.nodeLifetime.ApplicationStopping, repeatEvery: TimeSpans.RunOnce);
        }

        public override void Stop()
        {
            var cache = this.coinView as CachedCoinView;
            if (cache != null)
            {
                this.logger.LogInformation("Flushing Cache CoinView...");
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            this.asyncLoop.Dispose();
            this.dBreezeCoinView.Dispose();
        }

        private Task RunLoop(CancellationToken cancellationToken)
        {
            try
            {
                var stack = new CoinViewStack(this.coinView);
                var cache = stack.Find<CachedCoinView>();
                var stats = new ConsensusStats(stack, this.coinView, this.consensusLoop, this.chainState, this.chain, this.connectionManager, this.loggerFactory);

                ChainedBlock lastTip = this.consensusLoop.Tip;
                foreach (var block in this.consensusLoop.Execute(cancellationToken))
                {
                    if (this.consensusLoop.Tip.FindFork(lastTip) != lastTip)
                    {
                        this.logger.LogInformation("Reorg detected, rewinding from " + lastTip.Height + " (" + lastTip.HashBlock + ") to " + this.consensusLoop.Tip.Height + " (" + this.consensusLoop.Tip.HashBlock + ")");
                    }

                    lastTip = this.consensusLoop.Tip;

                    cancellationToken.ThrowIfCancellationRequested();

                    if (block.Error != null)
                    {
                        this.logger.LogError("Block rejected: " + block.Error.Message);

                        //Pull again
                        this.consensusLoop.Puller.SetLocation(this.consensusLoop.Tip);

                        if (block.Error == ConsensusErrors.BadWitnessNonceSize)
                        {
                            this.logger.LogInformation("You probably need witness information, activating witness requirement for peers.");
                            this.connectionManager.AddDiscoveredNodesRequirement(NodeServices.NODE_WITNESS);
                            this.consensusLoop.Puller.RequestOptions(TransactionOptions.Witness);
                            continue;
                        }

                        //Set the PoW chain back to ConsensusLoop.Tip
                        this.chain.SetTip(this.consensusLoop.Tip);
                        //Since ChainHeadersBehavior check PoW, MarkBlockInvalid can't be spammed
                        this.logger.LogError("Marking block as invalid");
                        this.chainState.MarkBlockInvalid(block.Block.GetHash());
                    }

                    if (block.Error == null)
                    {
                        this.chainState.HighestValidatedPoW = this.consensusLoop.Tip;
                        if (this.chain.Tip.HashBlock == block.ChainedBlock?.HashBlock)
                            this.consensusLoop.FlushAsync().GetAwaiter().GetResult();

                        this.signals.SignalBlock(block.Block);
                    }

                    // TODO: replace this with a signalling object
                    if (stats.CanLog)
                        stats.Log();
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    if (this.nodeLifetime.ApplicationStopping.IsCancellationRequested)
                        return Task.FromException(ex);
                }

                // TODO Need to revisit unhandled exceptions in a way that any process can signal an exception has been
                // thrown so that the node and all the disposables can stop gracefully.
                this.logger.LogDebug("Exception occurred in consensus loop: {0}", ex.ToString());
                this.logger.LogCritical(new EventId(0), ex, "Consensus loop unhandled exception (Tip:" + this.consensusLoop.Tip?.Height + ")");
                NLog.LogManager.Flush();
                throw;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A class providing extension methods for <see cref="IFullNodeBuilder"/>.
    /// </summary>
    public static partial class IFullNodeBuilderExtensions
    {
        public static IFullNodeBuilder UseConsensus(this IFullNodeBuilder fullNodeBuilder)
        {
            LoggingConfiguration.RegisterFeatureNamespace<ConsensusFeature>("consensus");
            LoggingConfiguration.RegisterFeatureClass<ConsensusStats>("bench");

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                .AddFeature<ConsensusFeature>()
                .FeatureServices(services =>
                {
                    // TODO: this should be set on the network build
                    fullNodeBuilder.Network.Consensus.Options = new PowConsensusOptions();

                    services.AddSingleton<NBitcoin.Consensus.ConsensusOptions, PowConsensusOptions>();
                    services.AddSingleton<PowConsensusValidator>();
                    services.AddSingleton<DBreezeCoinView>();
                    services.AddSingleton<CoinView, CachedCoinView>();
                    services.AddSingleton<LookaheadBlockPuller>();
                    services.AddSingleton<ConsensusLoop>();
                    services.AddSingleton<ConsensusManager>().AddSingleton<IBlockDownloadState, ConsensusManager>().AddSingleton<INetworkDifficulty, ConsensusManager>();
                    services.AddSingleton<IGetUnspentTransaction, ConsensusManager>();
                    services.AddSingleton<ConsensusController>();
                    services.AddSingleton<CacheSettings>(new CacheSettings());
                });
            });

            return fullNodeBuilder;
        }

        public static IFullNodeBuilder UseStratisConsensus(this IFullNodeBuilder fullNodeBuilder)
        {
            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                    .AddFeature<ConsensusFeature>()
                    .FeatureServices(services =>
                    {
                        fullNodeBuilder.Network.Consensus.Options = new PosConsensusOptions();

                        if (fullNodeBuilder.NodeSettings.Testnet)
                        {
                            fullNodeBuilder.Network.Consensus.Option<PosConsensusOptions>().COINBASE_MATURITY = 10;
                            fullNodeBuilder.Network.Consensus.Option<PosConsensusOptions>().StakeMinConfirmations = 10;
                        }

                        services.AddSingleton<PowConsensusValidator, PosConsensusValidator>();
                        services.AddSingleton<DBreezeCoinView>();
                        services.AddSingleton<CoinView, CachedCoinView>();
                        services.AddSingleton<LookaheadBlockPuller>();
                        services.AddSingleton<ConsensusLoop>();
                        services.AddSingleton<StakeChainStore>().AddSingleton<StakeChain, StakeChainStore>(provider => provider.GetService<StakeChainStore>());
                        services.AddSingleton<StakeValidator>();
                        services.AddSingleton<ConsensusManager>().AddSingleton<IBlockDownloadState, ConsensusManager>().AddSingleton<INetworkDifficulty, ConsensusManager>();
                        services.AddSingleton<ConsensusController>();
                        services.AddSingleton<CacheSettings>(new CacheSettings());
                    });
            });

            return fullNodeBuilder;
        }
    }
}