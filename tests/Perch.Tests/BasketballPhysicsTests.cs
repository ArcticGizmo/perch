using Perch.Games;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Exercises the desktop-basketball engine: the slingshot launch mapping, gravity/settling, wall
/// containment, the hoop colliders (rim lips + backboard) and swish detection. Everything is
/// deterministic, so these are plain step-until loops with generous iteration caps.
/// </summary>
public class BasketballPhysicsTests
{
    private const double Dt = 0.016;   // one 60fps frame, the cadence the app head steps at

    private static BasketballPhysics Court(double hoopX = 600, double rimY = 200, int facing = 1)
    {
        var p = new BasketballPhysics();
        p.SetBounds(800, 600);
        p.SetHoop(hoopX, rimY, facing);
        return p;
    }

    private static void StepUntilResting(BasketballPhysics p, int maxSteps = 4000)
    {
        for (int i = 0; i < maxSteps && !p.Resting; i++) p.Step(Dt);
        Assert.True(p.Resting, "ball never came to rest");
    }

    [Fact]
    public void LaunchVelocityFollowsTheDragAndIsCapped()
    {
        // Flick to the right → launch right; vertical always means up.
        var (vx, vy) = BasketballPhysics.LaunchVelocity(100, 100);
        Assert.True(vx > 0);
        Assert.True(vy < 0);

        // A drag past the cap launches no faster than the cap itself.
        var (cx, cy) = BasketballPhysics.LaunchVelocity(10_000, 0);
        var (mx, my) = BasketballPhysics.LaunchVelocity(240, 0);
        Assert.Equal(mx, cx, 3);
        Assert.Equal(my, cy, 3);
    }

    [Fact]
    public void VerticalDragAlwaysMeansUp()
    {
        // The ball rests on the bottom of the work area, so a downward flick has nowhere useful to go;
        // both vertical directions arc the shot up.
        var (ux, uy) = BasketballPhysics.LaunchVelocity(100, -100);
        var (dx, dy) = BasketballPhysics.LaunchVelocity(100, 100);
        Assert.Equal(dx, ux, 6);
        Assert.Equal(dy, uy, 6);
        Assert.True(uy < 0, "a vertical drag should always launch the ball upward");
    }

    [Fact]
    public void CatchFreezesTheBallMidFlightUntilWokenOrLaunched()
    {
        var p = Court();
        StepUntilResting(p);
        Assert.True(p.Launch(100, 100));
        for (int i = 0; i < 10; i++) p.Step(Dt);   // some way into the flight
        double x = p.X, y = p.Y;

        p.Catch();
        Assert.True(p.Resting);
        p.Step(Dt);                                 // frozen: hangs mid-air, gravity suspended
        Assert.Equal(x, p.X);
        Assert.Equal(y, p.Y);

        p.Wake();                                   // and falls again once woken
        p.Step(Dt);
        Assert.True(p.Y > y);
    }

    [Fact]
    public void ResetToRetossesTheBallFromThePointGivenAndWakesIt()
    {
        var p = Court();
        StepUntilResting(p);
        p.ResetTo(400, 300, 80, -120);
        Assert.False(p.Resting);
        Assert.Equal(400, p.X, 1);
        Assert.Equal(300, p.Y, 1);
        StepUntilResting(p);   // and it still settles like any other flight
    }

    [Fact]
    public void TinyDragIsACancelledShot()
    {
        var p = Court();
        StepUntilResting(p);
        Assert.False(p.Launch(2, -2));
        Assert.True(p.Resting);
    }

    [Fact]
    public void DroppedBallFallsSettlesOnTheFloorAndSleeps()
    {
        var p = Court();
        p.Drop(100);
        StepUntilResting(p);
        Assert.Equal(600 - BasketballPhysics.BallRadius, p.Y, 1);
        Assert.Equal(0, p.Vx, 3);
        Assert.Equal(0, p.Vy, 3);
    }

    [Fact]
    public void RestingBallIgnoresSteps()
    {
        var p = Court();
        StepUntilResting(p);
        double x = p.X, y = p.Y;
        var r = p.Step(Dt);
        Assert.Equal(default, r);
        Assert.Equal(x, p.X);
        Assert.Equal(y, p.Y);
    }

    [Fact]
    public void BallStaysInsideTheCourt()
    {
        var p = Court();
        StepUntilResting(p);
        Assert.True(p.Launch(200, 100));   // hard shot up-left, into the wall and ceiling
        for (int i = 0; i < 4000 && !p.Resting; i++)
        {
            p.Step(Dt);
            Assert.InRange(p.X, BasketballPhysics.BallRadius, 800 - BasketballPhysics.BallRadius);
            Assert.InRange(p.Y, BasketballPhysics.BallRadius, 600 - BasketballPhysics.BallRadius);
        }
    }

    [Fact]
    public void BallDroppedThroughTheRimScoresExactlyOnce()
    {
        var p = Court(hoopX: 300, rimY: 300, facing: 1);
        // Lips at 303 and 355 → the rim's centre line is x = 329. Drop straight through it.
        p.Drop(329);
        int scores = 0;
        for (int i = 0; i < 4000 && !p.Resting; i++)
            if (p.Step(Dt).Scored) scores++;
        Assert.Equal(1, scores);
    }

    [Fact]
    public void BallFallingOutsideTheRimDoesNotScore()
    {
        var p = Court(hoopX: 300, rimY: 300, facing: 1);
        p.Drop(500);   // well clear of the hoop
        for (int i = 0; i < 4000 && !p.Resting; i++)
            Assert.False(p.Step(Dt).Scored);
    }

    [Fact]
    public void UpwardCrossingDoesNotScoreOnTheWayUp()
    {
        // Fire the ball straight up through the rim from below: the upward pass must not score;
        // the fall back down through the rim is a legitimate basket.
        var p = Court(hoopX: 300, rimY: 300, facing: 1);
        p.Drop(329);
        StepUntilResting(p);
        // The swish on the way down already happened during the drop; now shoot straight up from under it.
        Assert.True(p.Launch(0, 200));   // vertical flick (either direction) → straight up
        for (int i = 0; i < 4000 && !p.Resting; i++)
        {
            var r = p.Step(Dt);
            if (r.Scored) Assert.True(p.Vy > 0, "scored while travelling upward");
        }
    }

    [Fact]
    public void FallingOntoARimLipReportsTheClangAndTheBallStillSettles()
    {
        var p = Court(hoopX: 300, rimY: 300, facing: 1);
        p.Drop(355);   // dead centre of the far lip
        bool clanged = false;
        for (int i = 0; i < 4000 && !p.Resting; i++)
            if (p.Step(Dt).RimHit) clanged = true;
        Assert.True(clanged);
        Assert.True(p.Resting);
    }

    [Fact]
    public void BackboardReflectsTheBall()
    {
        // Fly straight at the board's right face, above the rim line where no lip can intercept (the rim
        // faces left anyway). The board spans y 240..313 for a rim at 300.
        var p = Court(hoopX: 400, rimY: 300, facing: -1);
        p.ResetTo(500, 260, -1200, 0);
        bool cameBack = false;
        for (int i = 0; i < 600; i++)
        {
            p.Step(Dt);
            Assert.True(p.X >= 400 + BasketballPhysics.BallRadius - 0.5, "ball tunnelled through the backboard");
            if (p.Vx > 0) { cameBack = true; break; }
        }
        Assert.True(cameBack, "ball never bounced off the backboard");
    }

    [Fact]
    public void TrajectoryPreviewFollowsTheDragAndBendsDown()
    {
        var p = Court();
        StepUntilResting(p);
        var pts = p.TrajectoryPreview(120, 120, count: 12);   // flick right → shot up-right
        Assert.Equal(12, pts.Count);
        Assert.True(pts[0].X > p.X, "first point should follow the drag (right)");
        Assert.True(pts[0].Y < p.Y, "first point should arc upward");
        // Gravity bends the arc: consecutive vertical deltas grow (less negative → positive).
        double d1 = pts[1].Y - pts[0].Y;
        double dLast = pts[^1].Y - pts[^2].Y;
        Assert.True(dLast > d1, "the arc never bent downward");
    }

    [Fact]
    public void SetBoundsClampsTheBallBackInside()
    {
        var p = Court();
        StepUntilResting(p);   // resting on the floor at y ≈ 588
        p.SetBounds(400, 300);
        Assert.InRange(p.X, BasketballPhysics.BallRadius, 400 - BasketballPhysics.BallRadius);
        Assert.InRange(p.Y, BasketballPhysics.BallRadius, 300 - BasketballPhysics.BallRadius);
    }
}
