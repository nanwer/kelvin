// The tray icon as a temperature: a small rounded tile in the color that temperature has
// everywhere else in Kelvin (blue when cool, red when hot) with the number on it. Drawn by hand
// into a 32 x 32 image so it needs no font and stays sharp when Windows shows it at 16 x 16.

pub const SIZE: u32 = 32;

// Each digit on a 3 x 5 grid, one row per entry, most significant bit on the left.
const DIGITS: [[u8; 5]; 10] = [
    [0b111, 0b101, 0b101, 0b101, 0b111],
    [0b010, 0b110, 0b010, 0b010, 0b111],
    [0b111, 0b001, 0b111, 0b100, 0b111],
    [0b111, 0b001, 0b111, 0b001, 0b111],
    [0b101, 0b101, 0b111, 0b001, 0b001],
    [0b111, 0b100, 0b111, 0b001, 0b111],
    [0b111, 0b100, 0b111, 0b101, 0b111],
    [0b111, 0b001, 0b001, 0b001, 0b001],
    [0b111, 0b101, 0b111, 0b101, 0b111],
    [0b111, 0b101, 0b111, 0b001, 0b111],
];

// The same stops as the app's temperature scale (util.js and the lighting engine).
const SCALE: [(f64, [f64; 3]); 5] = [
    (35.0, [61.0, 123.0, 255.0]),
    (50.0, [158.0, 200.0, 255.0]),
    (62.0, [255.0, 226.0, 184.0]),
    (72.0, [255.0, 154.0, 61.0]),
    (85.0, [255.0, 46.0, 31.0]),
];

const INK: [u8; 3] = [13, 16, 24];

fn color(temp: f64) -> [u8; 3] {
    let mut out = SCALE[0].1;
    if temp >= SCALE[SCALE.len() - 1].0 {
        out = SCALE[SCALE.len() - 1].1;
    } else {
        for pair in SCALE.windows(2) {
            let ((t0, c0), (t1, c1)) = (pair[0], pair[1]);
            if temp > t0 && temp <= t1 {
                let k = (temp - t0) / (t1 - t0);
                out = [c0[0] + (c1[0] - c0[0]) * k, c0[1] + (c1[1] - c0[1]) * k, c0[2] + (c1[2] - c0[2]) * k];
            }
        }
    }
    [out[0].round() as u8, out[1].round() as u8, out[2].round() as u8]
}

/// How much of the pixel at (x, y) lies inside the rounded tile, from 0 to 1.
fn coverage(x: u32, y: u32) -> f64 {
    const RADIUS: f64 = 7.0;
    let size = SIZE as f64;
    let mut inside = 0;
    for (dx, dy) in [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)] {
        let (px, py) = (x as f64 + dx, y as f64 + dy);
        // Distance from the nearest corner's centre, where there is one to be near.
        let cx = px.clamp(RADIUS, size - RADIUS);
        let cy = py.clamp(RADIUS, size - RADIUS);
        if (px - cx).hypot(py - cy) <= RADIUS {
            inside += 1;
        }
    }
    inside as f64 / 4.0
}

/// The icon for a temperature in °C, as 32 x 32 RGBA. Shows 0 to 99.
pub fn render(temp: f64) -> Vec<u8> {
    let shown = temp.round().clamp(0.0, 99.0) as usize;
    let fill = color(temp);
    let mut px = vec![0u8; (SIZE * SIZE * 4) as usize];
    for y in 0..SIZE {
        for x in 0..SIZE {
            let i = ((y * SIZE + x) * 4) as usize;
            px[i..i + 3].copy_from_slice(&fill);
            px[i + 3] = (coverage(x, y) * 255.0).round() as u8;
        }
    }

    // Two digits fill the tile: each cell of a digit is 4 pixels wide and 4 tall.
    const CELL: u32 = 4;
    let digits: Vec<usize> = if shown >= 10 { vec![shown / 10, shown % 10] } else { vec![shown] };
    let width = digits.len() as u32 * 3 * CELL + (digits.len() as u32 - 1) * 2;
    let left = (SIZE - width) / 2;
    let top = (SIZE - 5 * CELL) / 2;
    for (n, digit) in digits.iter().enumerate() {
        let x0 = left + n as u32 * (3 * CELL + 2);
        for (row, bits) in DIGITS[*digit].iter().enumerate() {
            for col in 0..3u32 {
                if bits & (0b100 >> col) == 0 {
                    continue;
                }
                for dy in 0..CELL {
                    for dx in 0..CELL {
                        let (x, y) = (x0 + col * CELL + dx, top + row as u32 * CELL + dy);
                        let i = ((y * SIZE + x) * 4) as usize;
                        px[i..i + 3].copy_from_slice(&INK);
                    }
                }
            }
        }
    }
    px
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ink(px: &[u8]) -> usize {
        px.chunks(4).filter(|p| p[..3] == INK).count()
    }

    #[test]
    fn the_icon_is_a_full_tile_with_see_through_corners() {
        let px = render(54.0);
        assert_eq!(px.len(), (SIZE * SIZE * 4) as usize);
        assert_eq!(px[3], 0, "the very corner is transparent");
        let centre = ((16 * SIZE + 1) * 4) as usize;
        assert_eq!(px[centre + 3], 255, "the left edge, halfway down, is solid");
    }

    #[test]
    fn every_number_draws_differently_and_stays_inside_the_tile() {
        let mut seen = std::collections::HashSet::new();
        for t in 0..=99 {
            let px = render(t as f64);
            // Compare the digits only: the tile color changes with the temperature too.
            let shape: Vec<bool> = px.chunks(4).map(|p| p[..3] == INK).collect();
            assert!(seen.insert(shape), "{t} looks like another number");
            // No digit touches the outer two pixels.
            for (i, p) in px.chunks(4).enumerate() {
                let (x, y) = (i as u32 % SIZE, i as u32 / SIZE);
                if p[..3] == INK {
                    assert!((2..SIZE - 2).contains(&x) && (2..SIZE - 2).contains(&y), "{t} spills at {x},{y}");
                }
            }
        }
    }

    #[test]
    fn readings_outside_two_digits_are_capped_not_garbled() {
        assert_eq!(ink(&render(140.0)), ink(&render(99.0)));
        assert_eq!(ink(&render(-5.0)), ink(&render(0.0)));
    }

    #[test]
    fn the_tile_is_blue_when_cool_and_red_when_hot() {
        let cool = color(30.0);
        let hot = color(90.0);
        assert!(cool[2] > cool[0], "cool is blue");
        assert!(hot[0] > hot[2], "hot is red");
        assert_eq!(color(62.0), [255, 226, 184]);
    }
}
