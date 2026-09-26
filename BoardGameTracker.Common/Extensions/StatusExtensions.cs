using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.Enums;

namespace BoardGameTracker.Common.Extensions;

public static class StatusExtensions
{

    public static bool HasSupportedGameState(this CollectionResponse.Item item)
    {
        return item.Status.HasSupportedGameState() || item.NumPlays > 0;
    }

    public static GameState ToGameState(this CollectionResponse.Item item)
    {
        if (item.Status.HasSupportedGameState())
        {
            return item.Status.ToGameState();
        }

        if (item.NumPlays > 0)
        {
            return GameState.NotOwned;
        }

        throw new InvalidOperationException("BGG collection item has no supported BoardGameTracker state.");
    }

    public static bool HasSupportedGameState(this CollectionResponse.Status status)
    {
        return status.Owned
            || status.PreviouslyOwned
            || status.ForTrade
            || status.Want
            || status.WantToBuy
            || status.Wishlist
            || status.Preordered;
    }

    public static GameState ToGameState(this CollectionResponse.Status status)
    {
        if (status.PreviouslyOwned)
        {
            return GameState.PreviouslyOwned;
        }

        if (status.ForTrade)
        {
            return GameState.ForTrade;
        }

        if (status.Want || status.WantToBuy || status.Wishlist || status.Preordered)
        {
            return GameState.Wanted;
        }

        if (status.Owned)
        {
            return GameState.Owned;
        }

        throw new InvalidOperationException("BGG collection item has no supported BoardGameTracker state.");
    }
}
