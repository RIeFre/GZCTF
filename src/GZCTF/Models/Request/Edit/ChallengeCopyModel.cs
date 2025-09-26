using System.ComponentModel.DataAnnotations;

namespace GZCTF.Models.Request.Edit;

/// <summary>
/// Copy challenges from another game.
/// </summary>
public class ChallengeCopyModel
{
    /// <summary>
    /// Source game ID.
    /// </summary>
    [Required]
    public int SourceGameId { get; set; }

    /// <summary>
    /// Challenge IDs to copy. If empty, copy all challenges from the source game.
    /// </summary>
    public int[]? ChallengeIds { get; set; }
}

