using System.Collections.Generic;

namespace KuryerBakuBot
{
    // Stored in RAM (IMemoryCache) to temporarily track albums (Reverted to Stage 5)
    public class AlbumState
    {
        public string MediaGroupId { get; set; } = string.Empty;
        public long UserId { get; set; }
        public List<int> MessageIds { get; set; } = new();
        public bool IsViolated { get; set; }
    }
}