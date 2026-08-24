-- Perch — Connect 4 win-check fix
-- 20260821120000_connect4.sql created connect4_has_win() with `array_fill('n', array[7,6])` — the untyped 'n'
-- literal can't resolve array_fill's polymorphic element type, so the function fails at CALL time (on the first
-- move) with "could not determine polymorphic type". This forward-fix replaces the function: the fill value is
-- cast to text, and the board is a flat 42-cell array (index = col*6 + row + 1), which also sidesteps
-- PL/pgSQL's multidimensional-assignment quirks. Same logic and result as intended. Idempotent (create or
-- replace), so it applies cleanly on a fresh database (right after the base migration) or an existing one.

create or replace function public.connect4_has_win(p_game uuid)
returns boolean
language plpgsql
stable
security definer
set search_path = public
as $$
declare
  -- Flat 7x6 board, index = col*6 + row + 1 (col 0..6, row 0..5); 'r' | 'y' | 'n'.
  cell    text[] := array_fill('n'::text, array[42]);
  heights int[]  := array[0, 0, 0, 0, 0, 0, 0];
  m       record;
  base    text;
  c int; r int; d int; dc int; dr int; cc int; rr int; k int; cnt int;
  dirs int[] := array[1, 0, 0, 1, 1, 1, 1, -1];        -- four (dc,dr) axes, flattened
begin
  -- Replay moves in ply order; even ply = red, odd = yellow.
  for m in select col, ply from public.moves where game_id = p_game order by ply loop
    c := m.col;
    r := heights[c + 1];
    cell[c * 6 + r + 1] := case when m.ply % 2 = 0 then 'r' else 'y' end;
    heights[c + 1] := heights[c + 1] + 1;
  end loop;

  -- From every filled cell, count a run of the same colour along each of the four axes (positive direction
  -- only — every run is caught from its starting cell).
  for c in 0..6 loop
    for r in 0..5 loop
      base := cell[c * 6 + r + 1];
      if base = 'n' then continue; end if;
      for d in 0..3 loop
        dc := dirs[d * 2 + 1];
        dr := dirs[d * 2 + 2];
        cnt := 1; k := 1;
        loop
          cc := c + dc * k; rr := r + dr * k;
          exit when cc < 0 or cc > 6 or rr < 0 or rr > 5;
          exit when cell[cc * 6 + rr + 1] is distinct from base;
          cnt := cnt + 1; k := k + 1;
        end loop;
        if cnt >= 4 then return true; end if;
      end loop;
    end loop;
  end loop;
  return false;
end;
$$;
