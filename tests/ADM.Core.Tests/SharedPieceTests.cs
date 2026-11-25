using ADM.Core;

namespace ADM.Core.Tests;

public sealed class SharedPieceTests
{
    const int MB = 1024 * 1024;

    static byte[] Bytes(int count, byte value) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void Free_connection_takes_the_back_half_of_what_is_left()
    {
        var piece = new SharedPiece(10 * MB, 4 * MB);
        var own = piece.Take(out _)!;
        Assert.Equal(MB, piece.Store(own, Bytes(MB, 1)));

        var helper = piece.Take(out _)!;

        // Three megabytes were left: the owner keeps the first half and the helper asks for the rest.
        Assert.Equal((10 * MB + MB, 10 * MB + MB + 3 * MB / 2 - 1), piece.RangeOf(own));
        Assert.Equal((10 * MB + MB + 3 * MB / 2, 14 * MB - 1), piece.RangeOf(helper));
    }

    [Fact]
    public void Bytes_past_a_moved_end_are_left_to_the_span_beyond_it()
    {
        var piece = new SharedPiece(0, 2 * MB);
        var own = piece.Take(out _)!;
        var helper = piece.Take(out _)!;

        // The owner's request still runs to the old end; what it reads past the split is not its to keep.
        Assert.Equal(MB, piece.Store(own, Bytes(MB + 1000, 1)));
        Assert.Equal(0, piece.LeftOf(own));
        Assert.Equal(MB, piece.Store(helper, Bytes(MB, 2)));

        Assert.True(piece.IsComplete);
        Assert.Equal(Bytes(MB, 1).Concat(Bytes(MB, 2)), piece.Data);
    }

    [Fact]
    public void Too_little_left_is_not_split()
    {
        var piece = new SharedPiece(0, 2 * SharedPiece.MinSplitBytes - 1);
        Assert.NotNull(piece.Take(out _));

        Assert.Null(piece.Take(out var released));
        Assert.False(released.IsCompleted);
    }

    [Fact]
    public void Span_let_go_is_taken_next_from_where_it_stopped()
    {
        // Split once, each half has too little left to split again.
        var piece = new SharedPiece(0, 2 * MB - 2);
        piece.Take(out _);
        var helper = piece.Take(out _)!;
        piece.Store(helper, Bytes(1000, 2));
        Assert.Null(piece.Take(out var released));

        piece.Release(helper);

        Assert.True(released.IsCompleted);
        var again = piece.Take(out _)!;
        Assert.Equal((MB - 1 + 1000, 2 * MB - 3), piece.RangeOf(again));
    }

    [Fact]
    public void Complete_piece_has_nothing_to_wait_for()
    {
        var piece = new SharedPiece(0, 1000);
        var own = piece.Take(out _)!;
        piece.Store(own, Bytes(1000, 3));
        piece.Release(own);

        Assert.Null(piece.Take(out var released));
        Assert.True(released.IsCompleted);
    }

    [Fact]
    public void Abandoned_piece_keeps_nothing_more()
    {
        var piece = new SharedPiece(0, 2 * MB);
        var own = piece.Take(out _)!;
        piece.Store(own, Bytes(5000, 1));

        Assert.Equal(5000, piece.Abandon());
        Assert.Equal(0, piece.Store(own, Bytes(5000, 1)));
    }
}
