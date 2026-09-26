using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public class EditOperationsTests
{
    private static void AssertRect(CropRect expected, CropRect? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual.Value.X, 6);
        Assert.Equal(expected.Y, actual.Value.Y, 6);
        Assert.Equal(expected.Width, actual.Value.Width, 6);
        Assert.Equal(expected.Height, actual.Value.Height, 6);
    }

    [Fact]
    public void Rotating_keeps_the_same_region_selected()
    {
        // Top-left quarter of a landscape image...
        var ops = new EditOperations { Crop = new CropRect(0, 0, 0.5, 0.5) };

        // ...is the top-right quarter after a clockwise turn.
        AssertRect(new CropRect(0.5, 0, 0.5, 0.5), ops.RotateClockwise().Crop);
        // Four turns return to the start.
        var back = ops.RotateClockwise().RotateClockwise().RotateClockwise().RotateClockwise();
        Assert.Equal(0, back.Rotation);
        AssertRect(ops.Crop.Value, back.Crop);
        // Clockwise then counter-clockwise is a no-op.
        AssertRect(ops.Crop.Value, ops.RotateClockwise().RotateCounterClockwise().Crop);
    }

    [Fact]
    public void Output_size_accounts_for_rotation_and_crop()
    {
        var ops = new EditOperations { Rotation = 90, Crop = new CropRect(0, 0, 0.5, 0.25) };

        Assert.Equal((1500, 1000), ops.OutputSize(4000, 3000));
    }

    [Fact]
    public void Visible_flip_of_rotated_image_mirrors_the_crop()
    {
        var ops = new EditOperations { Rotation = 90, Crop = new CropRect(0.1, 0.2, 0.3, 0.4) };

        var flipped = ops.FlipVisible();

        Assert.True(flipped.FlipHorizontal);
        Assert.Equal(270, flipped.Rotation);
        AssertRect(new CropRect(0.6, 0.2, 0.3, 0.4), flipped.Crop);
    }

    [Fact]
    public void Json_round_trips_and_omits_defaults()
    {
        var ops = new EditOperations { Rotation = 270, Exposure = 0.5, Crop = new CropRect(0.1, 0.2, 0.3, 0.4) };

        var json = ops.ToJson();

        Assert.DoesNotContain("Brightness", json);
        Assert.Equal(ops, EditOperations.FromJson(json));
    }

    [Fact]
    public void Identity_detection()
    {
        Assert.True(EditOperations.None.IsIdentity);
        Assert.True(new EditOperations { Rotation = 0, Crop = CropRect.Full }.IsIdentity);
        Assert.False(new EditOperations { Contrast = 0.2 }.IsIdentity);
        Assert.True(new EditOperations().RotateClockwise().RotateCounterClockwise().IsIdentity);
    }

    [Fact]
    public void Clamp_keeps_crop_inside_image()
    {
        var clamped = new CropRect(0.9, -0.2, 0.5, 0.001).Clamp();

        AssertRect(new CropRect(0.5, 0, 0.5, 0.02), clamped);
    }
}
