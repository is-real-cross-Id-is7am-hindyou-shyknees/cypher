﻿//CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using CYPCore.Extensions;
using Serilog;

using CYPCore.Models;
using CYPCore.Network;
using CYPCore.Persistence;
using CYPCore.Services.Rest;

namespace CYPCore.Ledger
{
    /// <summary>
    /// 
    /// </summary>
    public interface ISync
    {
        bool SyncRunning { get; }

        Task Check();
        Task Synchronize(Uri uri, long skip, long take);
    }
    
    /// <summary>
    /// 
    /// </summary>
    public class Sync : ISync
    {
        public bool SyncRunning { get; private set; }

        private const int BatchSize = 20;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IValidator _validator;
        private readonly ILocalNode _localNode;
        private readonly ILogger _logger;

        public Sync(IUnitOfWork unitOfWork, IValidator validator, ILocalNode localNode, ILogger logger)
        {
            _unitOfWork = unitOfWork;
            _validator = validator;
            _localNode = localNode;
            _logger = logger.ForContext("SourceContext", nameof(Sync));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task Check()
        {
            SyncRunning = true;

            try
            {
                _logger.Here().Information("Checking block height");

                var peers = await _localNode.GetPeers();
                foreach (var peer in peers)
                {
                    try
                    {
                        var local = new BlockHeight { Height = await _unitOfWork.DeliveredRepository.CountAsync() };
                        var uri = new Uri(peer.Value.Host);

                        RestBlockService blockRestApi = new(uri);
                        var remote = await blockRestApi.GetHeight();

                        _logger.Here().Information("Local node block height ({@LocalHeight}). Network block height ({NetworkHeight})",
                            local.Height,
                            remote.Height);

                        if (local.Height < remote.Height)
                        {
                            await Synchronize(uri, local.Height, remote.Height / peers.Count);
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }
                    catch (TaskCanceledException)
                    {
                    }
                    catch (Refit.ApiException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Error while checking");
            }

            SyncRunning = false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task Synchronize(Uri uri, long skip, long take)
        {
            var throttler = new SemaphoreSlim(int.MaxValue);
            await throttler.WaitAsync();

            try
            {
                var tasks = new List<Task>();
                var numberOfBatches = (int)Math.Ceiling((double)take / BatchSize);

                for (var i = 0; i < numberOfBatches; i++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var blockRestApi = new RestBlockService(uri);
                            var blockHeaderStream = await blockRestApi.GetBlockHeaders((int)(i * skip), BatchSize);

                            if (blockHeaderStream.Protobufs.Any())
                            {
                                var blockHeaders =
                                    Helper.Util.DeserializeListProto<BlockHeaderProto>(blockHeaderStream.Protobufs);

                                foreach (var blockHeader in blockHeaders)
                                {
                                    try
                                    {
                                        await _validator.GetRunningDistribution();

                                        var verified = await _validator.VerifyBlockHeader(blockHeader);
                                        if (!verified)
                                        {
                                            return;
                                        }

                                        var saved = await _unitOfWork.DeliveredRepository.PutAsync(
                                            blockHeader.ToIdentifier(), blockHeader);
                                        if (!saved)
                                        {
                                            _logger.Here().Error("Unable to save block header: {@MerkleRoot}",
                                                blockHeader.MrklRoot);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Here().Error(ex, "Unable to save block header: {@MerkleRoot}",
                                            blockHeader.MrklRoot);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Failed to synchronize node");
            }
            finally
            {
                throttler.Dispose();
            }
        }
    }
}