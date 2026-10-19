"""sg.geom: grid cells and boxes, in pure Python (no hub call).

The two grids (the mod's own grid helpers):

- size 2, the large grid: 2 m cells whose centres sit on odd metres, the cell around x spanning [x-1, x+1]
  (Pure/Layout/LargeCells.cs Centre: floor(m / 2) * 2 + 1);
- size 0.5, the small grid: 0.5 m cells whose centres sit on multiples of 0.5 m, the cell around x spanning
  [x-0.25, x+0.25] (Pure/Routing/GridStep.cs: Grid3(position, 0.5, 0.25), round((p - 0.5) / 0.5) * 0.5 + 0.5).

A box is two corner points [[x, y, z], [x, y, z]] in any order (the verbs' box option; a point may also be {x, y, z}).
It covers every cell whose centre lies inside it, sides included. A box this module returns is the cells' outer
bounds, so cells(box) gives back exactly the cells it was made from, and a verb given that box (find, survey, remove
box) takes the same cells: a side lying on a face plane takes no cell beyond it (LargeCells.InBox).

    sg.geom.cells([[0, 0, 0], [4, 2, 2]])          # [(1, 1, 1), (3, 1, 1)]
    sg.geom.merge_rects([(1, 1, 1), (3, 1, 1)])    # [[[0, 0, 0], [4, 2, 2]]]
    sg.geom.tiles([[0, 0, 0], [20, 2, 20]], 25)   # boxes of at most 25 cells, covering all 100
"""
import math
from collections.abc import Mapping

# A cell size and where its centres sit: centre = offset + index * size.
_OFFSETS = {2: 1.0, 0.5: 0.0}
_EDGE = 1e-6


def _offset(size):
    for known, offset in _OFFSETS.items():
        if abs(size - known) < _EDGE:
            return offset
    raise ValueError(f"size {size!r}: the grids are 2 (large, 2 m) and 0.5 (small, 0.5 m)")


def _point(point):
    if isinstance(point, Mapping):
        point = (point["x"], point["y"], point["z"])
    x, y, z = point
    return float(x), float(y), float(z)


def _corners(box):
    a, b = box
    return _point(a), _point(b)


def _number(value):
    """value as an int when it is whole, else a float rounded to the grid's precision."""
    rounded = round(value, 6)
    return int(rounded) if rounded == int(rounded) else rounded


def _index(coordinate, size, offset):
    """The cell index nearest a coordinate (a cell centre, give or take a rounding error)."""
    return round((float(coordinate) - offset) / size)


def _centre(index, size, offset):
    return _number(offset + index * size)


def _span(low, high, size, offset):
    """The first and last cell indices whose centres lie in [low, high], sides included (last < first: none)."""
    low, high = min(low, high), max(low, high)
    return math.ceil((low - offset) / size - _EDGE), math.floor((high - offset) / size + _EDGE)


def _ranges(box, size):
    offset = _offset(size)
    a, b = _corners(box)
    return [_span(a[axis], b[axis], size, offset) for axis in range(3)]


def _box(low, high, size, offset):
    """The outer bounds of the cells from index low to high on each axis, as [[x, y, z], [x, y, z]]."""
    return [[_number(offset + low[axis] * size - size / 2) for axis in range(3)],
            [_number(offset + high[axis] * size + size / 2) for axis in range(3)]]


def cells(box, size=2):
    """The centres (x, y, z) of every cell whose centre lies inside box, sides included; y, then z, then x ascending."""
    offset = _offset(size)
    (x0, x1), (y0, y1), (z0, z1) = _ranges(box, size)
    return [(_centre(x, size, offset), _centre(y, size, offset), _centre(z, size, offset))
            for y in range(y0, y1 + 1) for z in range(z0, z1 + 1) for x in range(x0, x1 + 1)]


def merge_rects(cells, size=2):
    """Boxes (outer bounds) that together cover exactly the given cells, each cell in one box. Greedy: from the
    lowest cell left (y, then z, then x), a box grows along x while the next cell is there, then along z while the
    whole next row is, then along y while the whole next layer is. A cell is a centre (x, y, z) or {x, y, z}; a
    point off a centre counts as the nearest centre."""
    offset = _offset(size)
    left = set()
    for cell in cells:
        x, y, z = _point(cell)
        left.add((_index(x, size, offset), _index(y, size, offset), _index(z, size, offset)))
    boxes = []
    for start in sorted(left, key=lambda c: (c[1], c[2], c[0])):
        if start not in left:
            continue
        x0, y0, z0 = start
        x1 = x0
        while (x1 + 1, y0, z0) in left:
            x1 += 1
        z1 = z0
        while all((x, y0, z1 + 1) in left for x in range(x0, x1 + 1)):
            z1 += 1
        y1 = y0
        while all((x, y1 + 1, z) in left for x in range(x0, x1 + 1) for z in range(z0, z1 + 1)):
            y1 += 1
        for y in range(y0, y1 + 1):
            for z in range(z0, z1 + 1):
                for x in range(x0, x1 + 1):
                    left.discard((x, y, z))
        boxes.append(_box((x0, y0, z0), (x1, y1, z1), size, offset))
    return boxes


def _parts(count, most):
    """count split into the fewest runs of at most `most`, as even as they go: [(first, last), ...] offsets."""
    pieces = -(-count // most)
    runs, first = [], 0
    for piece in range(pieces):
        length = count // pieces + (1 if piece < count % pieces else 0)
        runs.append((first, first + length - 1))
        first += length
    return runs


def tiles(box, max_cells, size=2):
    """Boxes (outer bounds) that each hold at most max_cells cells and together cover exactly the cells of box, each
    cell in one box: the box cut along x first, then z, then y, into tiles of even size. [] when box holds no cell."""
    if isinstance(max_cells, bool) or not isinstance(max_cells, int) or max_cells < 1:
        raise ValueError(f"max_cells {max_cells!r}: a whole number of cells, at least 1")
    offset = _offset(size)
    (x0, x1), (y0, y1), (z0, z1) = _ranges(box, size)
    nx, ny, nz = x1 - x0 + 1, y1 - y0 + 1, z1 - z0 + 1
    if nx <= 0 or ny <= 0 or nz <= 0:
        return []
    tx = min(nx, max_cells)
    tz = min(nz, max_cells // tx)
    ty = min(ny, max_cells // (tx * tz))
    boxes = []
    for ya, yb in _parts(ny, ty):
        for za, zb in _parts(nz, tz):
            for xa, xb in _parts(nx, tx):
                boxes.append(_box((x0 + xa, y0 + ya, z0 + za), (x0 + xb, y0 + yb, z0 + zb), size, offset))
    return boxes
