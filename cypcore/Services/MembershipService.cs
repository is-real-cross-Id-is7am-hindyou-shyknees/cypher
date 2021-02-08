﻿// CYPNode by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

using CYPCore.Cryptography;
using CYPCore.Serf;
using CYPCore.Serf.Message;

namespace CYPCore.Services
{
    public class MembershipService : IMembershipService
    {
        private readonly ISerfClient _serfClient;
        private readonly ISigning _signingProvider;
        private readonly ILogger _logger;

        private TcpSession _tcpSession;

        /// <summary>
        ///
        /// </summary>
        /// <param name="serfClient"></param>
        /// <param name="signingProvider"></param>
        /// <param name="logger"></param>
        public MembershipService(ISerfClient serfClient, ISigning signingProvider, ILogger<MembershipService> logger)
        {
            _serfClient = serfClient;
            _signingProvider = signingProvider;
            _logger = logger;

            Ready();
        }

        /// <summary>
        /// 
        /// </summary>
        public void Ready()
        {
            _tcpSession = _serfClient.TcpSessionsAddOrUpdate(
                new TcpSession(_serfClient.SerfConfigurationOptions.Listening)
                .Connect(_serfClient.SerfConfigurationOptions.RPC));
        }

        /// <summary>
        /// 
        /// </summary>
        public async Task<IEnumerable<Members>> GetMembers()
        {
            var members = Enumerable.Empty<Members>();

            try
            {
                var tcpSession = _serfClient.GetTcpSession(_tcpSession.SessionId);
                if (tcpSession.Ready)
                {
                    var connectResult = _serfClient.Connect(tcpSession.SessionId);
                    if (!connectResult.Result.Success)
                    {
                        _logger.LogError($"Error connecting to {tcpSession.SessionId.ToString()}");
                        return null;
                    }

                    var membersResult = await _serfClient.Members(tcpSession.SessionId);
                    if (!membersResult.Success)
                    {
                        return null;
                    }

                    members = membersResult.Value.Members;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"<<< MembershipService.GetMembers >>>: {ex}");
            }

            return members;
        }

        /// <summary>
        /// 
        /// </summary>
        public async Task<byte[]> GetPublicKey()
        {
            byte[] publicKey = null;

            try
            {
                publicKey = await _signingProvider.GetPublicKey(_signingProvider.DefaultSigningKeyName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"<<< MembershipService.GetPublicKey >>>: {ex}");
            }

            return publicKey;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task<int> GetCount()
        {
            int count = 0;

            try
            {
                var members = await GetMembers();
                if (members.Any())
                {
                    count = members.Count(x => x.Status.Equals("alive"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"<<< MembershipService.GetCount >>>: {ex}");
            }

            return count;
        }
    }
}
