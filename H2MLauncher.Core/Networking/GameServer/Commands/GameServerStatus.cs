﻿using System.Net;

namespace H2MLauncher.Core.Networking.GameServer
{
    public record GameServerStatus
    {
        public required IPEndPoint Address { get; init; }

        public List<GamePlayerStatus> Players { get; init; } = [];

        /// <summary>
        /// Calculates the total score of all players.
        /// </summary>
        public int TotalScore => Players.Sum(p => p.Score);
    }
}
