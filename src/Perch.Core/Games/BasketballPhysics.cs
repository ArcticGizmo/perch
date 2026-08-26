namespace Perch.Games;

/// <summary>What happened during one <see cref="BasketballPhysics.Step"/>: a swish (the ball dropped
/// cleanly through the rim), a rim-lip clang, and/or a bounce off a wall, the floor or the backboard.
/// The view uses these for feedback; the tally only cares about <see cref="Scored"/>.</summary>
public readonly record struct BasketballStepResult(bool Scored, bool RimHit, bool Bounced);

/// <summary>
/// The pure, UI-free physics behind the desktop basketball toy (see <c>BasketballWindow</c> in the app
/// head): one ball bouncing around a rectangular court (the screen's work area, in DIPs, origin top-left,
/// +y down) with gravity, air drag and restitution; a hoop made of a vertical backboard segment and two
/// rim-lip point colliders; swish detection when the ball's centre crosses the rim line downward between
/// the lips; and the flick launch (drag toward the target, capped, vertical always up). Kept in
/// <c>Perch.Core</c> so it's deterministic and testable like <see cref="Connect4Game"/> — nothing here
/// touches Avalonia or the OS. <see cref="Step"/> sub-steps internally so a fast ball can't tunnel
/// through a rim lip between frames.
/// </summary>
public sealed class BasketballPhysics
{
    // ── Court / ball geometry (DIPs) ─────────────────────────────────────────
    public const double BallRadius = 12;
    /// <summary>Rim opening, backboard face to front lip. Comfortably wider than the ball (real hoops are
    /// ~1.9× the ball; this is ~1.7× so shots are makeable but not automatic).</summary>
    public const double RimSpan = 40;
    /// <summary>How far the rim's near lip sits off the backboard face.</summary>
    public const double RimInset = 2;
    /// <summary>Backboard extent above / below the rim line.</summary>
    public const double BoardAboveRim = 46;
    public const double BoardBelowRim = 10;

    // ── Feel constants ───────────────────────────────────────────────────────
    private const double Gravity = 1800;          // DIP/s²
    private const double MaxDrag = 240;           // drag length (DIP) that maps to full power
    private const double MaxSpeed = 1900;         // DIP/s at full power
    public const double MinDrag = 6;              // shorter drags are a cancelled shot
    private const double WallRestitution = 0.72;
    private const double FloorRestitution = 0.62;
    private const double BoardRestitution = 0.65;
    private const double RimRestitution = 0.5;
    private const double AirDrag = 0.10;          // fraction of velocity shed per second
    private const double RollFriction = 2.2;      // fraction of ground speed shed per second
    private const double SleepSpeed = 26;         // slower than this on the floor → asleep
    private const double SubStep = 0.004;         // internal integration step (s); 1900·0.004 ≈ 7.6 < BallRadius
    private const double MaxStep = 0.05;          // clamp a hitched frame so the ball doesn't teleport

    // ── Court bounds (the layer window's size in DIPs) ───────────────────────
    public double Width { get; private set; } = 800;
    public double Height { get; private set; } = 600;

    // ── Hoop: backboard plane x, rim height y, and which way the rim opens ───
    public double BoardX { get; private set; } = 200;
    public double RimY { get; private set; } = 200;
    /// <summary>+1 when the rim extends to the right of the backboard, -1 to the left.</summary>
    public int Facing { get; private set; } = 1;
    public double RimNearX => BoardX + Facing * RimInset;
    public double RimFarX => BoardX + Facing * (RimInset + RimSpan);

    // ── Ball state ───────────────────────────────────────────────────────────
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Vx { get; private set; }
    public double Vy { get; private set; }
    /// <summary>True once the ball has settled on the floor; <see cref="Step"/> is a no-op until the next
    /// <see cref="Launch"/> (or <see cref="Drop"/>).</summary>
    public bool Resting { get; private set; }

    private bool _wasAboveRim;   // armed above the rim line, spent by a swish — one score per descent

    public BasketballPhysics() => Drop(120);

    /// <summary>Resize the court (the work area changed); the ball is clamped back inside.</summary>
    public void SetBounds(double width, double height)
    {
        Width = Math.Max(width, BallRadius * 4);
        Height = Math.Max(height, BallRadius * 4);
        X = Math.Clamp(X, BallRadius, Width - BallRadius);
        Y = Math.Clamp(Y, BallRadius, Height - BallRadius);
    }

    /// <summary>Move the hoop (the overlay panel moved or resized). The ball is unaffected.</summary>
    public void SetHoop(double boardX, double rimY, int facing)
    {
        BoardX = boardX;
        RimY = rimY;
        Facing = facing >= 0 ? 1 : -1;
    }

    /// <summary>Drop the ball from the top of the court at <paramref name="x"/> (spawn / respawn).</summary>
    public void Drop(double x)
    {
        X = Math.Clamp(x, BallRadius, Width - BallRadius);
        Y = BallRadius;
        Vx = 0;
        Vy = 0;
        Resting = false;
        _wasAboveRim = false;
    }

    /// <summary>The flick launch velocity for a drag of (<paramref name="dragDx"/>,
    /// <paramref name="dragDy"/>): <em>toward</em> the drag (flick where you want the ball to go), speed
    /// proportional to the drag's length, capped. The vertical component always means <em>up</em>
    /// (mirrored): the ball rests on the very bottom of the work area, and a launch into the floor is
    /// never what anyone meant anyway.</summary>
    public static (double Vx, double Vy) LaunchVelocity(double dragDx, double dragDy)
    {
        double dy = Math.Abs(dragDy);
        double len = Math.Sqrt(dragDx * dragDx + dy * dy);
        if (len < MinDrag) return (0, 0);
        double speed = Math.Min(len, MaxDrag) / MaxDrag * MaxSpeed;
        return (dragDx / len * speed, -dy / len * speed);
    }

    /// <summary>Wake a resting ball so gravity applies again — for when the surface it settled on moved
    /// (the user dragged the rim it was balanced on). A no-op mid-flight.</summary>
    public void Wake() => Resting = false;

    /// <summary>Re-toss the ball from an arbitrary point with an arbitrary velocity (the hoop's
    /// double-click reset; the view adds the variance so the engine stays deterministic).</summary>
    public void ResetTo(double x, double y, double vx, double vy)
    {
        X = Math.Clamp(x, BallRadius, Width - BallRadius);
        Y = Math.Clamp(y, BallRadius, Height - BallRadius);
        Vx = vx;
        Vy = vy;
        Resting = false;
        _wasAboveRim = false;
    }

    /// <summary>Fire the ball opposite the drag vector. A drag shorter than <see cref="MinDrag"/> is a
    /// cancelled shot (returns false, nothing changes).</summary>
    public bool Launch(double dragDx, double dragDy)
    {
        var (vx, vy) = LaunchVelocity(dragDx, dragDy);
        if (vx == 0 && vy == 0) return false;
        Vx = vx;
        Vy = vy;
        Resting = false;
        _wasAboveRim = false;
        return true;
    }

    /// <summary>The first <paramref name="count"/> points of the flight a launch would take from the
    /// resting ball — gravity and drag only, no collisions — sampled every <paramref name="sampleSeconds"/>.
    /// Deliberately partial: the aim line hints at the arc without giving the whole shot away.</summary>
    public IReadOnlyList<(double X, double Y)> TrajectoryPreview(
        double dragDx, double dragDy, int count = 10, double sampleSeconds = 0.045)
    {
        var points = new List<(double, double)>(count);
        var (vx, vy) = LaunchVelocity(dragDx, dragDy);
        if (vx == 0 && vy == 0) return points;

        double x = X, y = Y;
        for (int i = 0; i < count; i++)
        {
            for (double t = 0; t < sampleSeconds; t += SubStep)
            {
                vy += Gravity * SubStep;
                double keep = 1 - AirDrag * SubStep;
                vx *= keep;
                vy *= keep;
                x += vx * SubStep;
                y += vy * SubStep;
            }
            points.Add((x, y));
        }
        return points;
    }

    /// <summary>Advance the simulation by <paramref name="dtSeconds"/> (clamped, internally sub-stepped).
    /// A resting ball is a no-op.</summary>
    public BasketballStepResult Step(double dtSeconds)
    {
        if (Resting || dtSeconds <= 0) return default;

        bool scored = false, rimHit = false, bounced = false;
        double remaining = Math.Min(dtSeconds, MaxStep);
        while (remaining > 0)
        {
            double dt = Math.Min(remaining, SubStep);
            remaining -= dt;
            var r = SubStepOnce(dt);
            scored |= r.Scored;
            rimHit |= r.RimHit;
            bounced |= r.Bounced;
            if (Resting) break;
        }
        return new BasketballStepResult(scored, rimHit, bounced);
    }

    private BasketballStepResult SubStepOnce(double dt)
    {
        bool scored = false, rimHit = false, bounced = false;

        Vy += Gravity * dt;
        double keep = 1 - AirDrag * dt;
        Vx *= keep;
        Vy *= keep;

        double prevY = Y;
        X += Vx * dt;
        Y += Vy * dt;

        // Walls and ceiling.
        if (X < BallRadius) { X = BallRadius; Vx = Math.Abs(Vx) * WallRestitution; bounced = true; }
        else if (X > Width - BallRadius) { X = Width - BallRadius; Vx = -Math.Abs(Vx) * WallRestitution; bounced = true; }
        if (Y < BallRadius) { Y = BallRadius; Vy = Math.Abs(Vy) * WallRestitution; bounced = true; }

        // Backboard: a vertical segment either side of whose plane the ball reflects.
        double boardTop = RimY - BoardAboveRim, boardBottom = RimY + BoardBelowRim;
        if (Math.Abs(X - BoardX) < BallRadius && Y > boardTop - BallRadius && Y < boardBottom + BallRadius)
        {
            int side = X >= BoardX ? 1 : -1;              // which face it came in on
            if (Math.Sign(Vx) != side)                    // only if moving into the plane
            {
                X = BoardX + side * BallRadius;
                Vx = side * Math.Abs(Vx) * BoardRestitution;
                bounced = true;
            }
        }

        // Rim lips: two point colliders. Push out along the contact normal, reflect the normal component.
        double supportNy = 0;
        if (CollideLip(RimNearX, ref supportNy)) rimHit = true;
        if (CollideLip(RimFarX, ref supportNy)) rimHit = true;
        // A slow ball supported from below by a lip sleeps there (balanced on the rim) — otherwise a
        // perfectly-centred ball would jiggle against the lip forever and the tick timer would never stop.
        if (supportNy < -0.7 && Math.Sqrt(Vx * Vx + Vy * Vy) < SleepSpeed)
        {
            Vx = 0;
            Vy = 0;
            Resting = true;
        }

        // Swish: armed above the rim, centre crosses the rim line downward between the lips.
        double lipMin = Math.Min(RimNearX, RimFarX), lipMax = Math.Max(RimNearX, RimFarX);
        if (Y < RimY - 4) _wasAboveRim = true;
        if (_wasAboveRim && Vy > 0 && prevY <= RimY && Y > RimY && X > lipMin + 3 && X < lipMax - 3)
        {
            scored = true;
            _wasAboveRim = false;
        }

        // Floor: bounce, then roll, then sleep.
        if (Y > Height - BallRadius)
        {
            Y = Height - BallRadius;
            double vyOut = Math.Abs(Vy) * FloorRestitution;
            Vy = vyOut < 40 ? 0 : -vyOut;                 // too slow to bounce → grounded
            bounced = true;
        }
        bool grounded = Y >= Height - BallRadius - 0.5 && Vy == 0;
        if (grounded)
        {
            Vx *= Math.Max(0, 1 - RollFriction * dt);
            if (Math.Abs(Vx) < SleepSpeed)
            {
                Vx = 0;
                Resting = true;
            }
        }

        return new BasketballStepResult(scored, rimHit, bounced);
    }

    private bool CollideLip(double lipX, ref double supportNy)
    {
        double dx = X - lipX, dy = Y - RimY;
        double distSq = dx * dx + dy * dy;
        if (distSq >= BallRadius * BallRadius || distSq < 1e-9) return false;

        double dist = Math.Sqrt(distSq);
        double nx = dx / dist, ny = dy / dist;
        supportNy = Math.Min(supportNy, ny);
        // Push the ball out of the lip, then reflect the incoming normal component with rim restitution.
        X = lipX + nx * BallRadius;
        Y = RimY + ny * BallRadius;
        double vn = Vx * nx + Vy * ny;
        if (vn < 0)
        {
            Vx -= (1 + RimRestitution) * vn * nx;
            Vy -= (1 + RimRestitution) * vn * ny;
        }
        return true;
    }
}
