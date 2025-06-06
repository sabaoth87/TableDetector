using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TableDetector.Models;

namespace TableDetector.Services
{
    public class TokenTrackingService
    {
        private readonly double maxDistanceThreshold;
        private readonly Dictionary<Guid, TTRPGToken> lastKnownTokens = new Dictionary<Guid, TTRPGToken>();

        public TokenTrackingService(double maxDistance = 50.0)
        {
            maxDistanceThreshold = maxDistance;
        }

        public List<TTRPGToken> Track(List<TTRPGToken> currentDetections)
        {
            var updatedTokens = new List<TTRPGToken>();
            var matchedLastFrame = new HashSet<Guid>();

            foreach (var current in currentDetections)
            {
                var match = lastKnownTokens
                    .Where(kvp => !matchedLastFrame.Contains(kvp.Key))
                    .Select(kvp => new
                    {
                        Id = kvp.Key,
                        Token = kvp.Value,
                        Distance = (current.Position - kvp.Value.Position).Length
                    })
                    .Where(x => x.Distance < maxDistanceThreshold)
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();

                if (match != null)
                {
                    current.Id = match.Id;
                    matchedLastFrame.Add(match.Id);
                }
                else
                {
                    current.Id = Guid.NewGuid();
                }

                updatedTokens.Add(current);
                lastKnownTokens[current.Id] = current;
            }

            return updatedTokens;
        }
    }
}
