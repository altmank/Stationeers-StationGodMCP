"""sg.geom: cells, merge_rects and tiles on both grids, with property tests over random cell sets."""
import random
import unittest

import fakehub  # noqa: F401 - puts the package on sys.path
import sg
from sg import geom

SEED = 20261018


def cells_of(boxes, size):
    """Every cell of every box, with repeats (a cell in two boxes shows twice)."""
    return [cell for box in boxes for cell in geom.cells(box, size)]


class CellsTests(unittest.TestCase):
    def test_large_cells_are_centred_on_odd_metres(self):
        self.assertEqual([(1, 1, 1), (3, 1, 1)], geom.cells([[0, 0, 0], [4, 2, 2]]))
        self.assertEqual([(-1, 1, 1)], geom.cells([[-2, 0, 0], [0, 2, 2]]))

    def test_a_box_takes_every_centre_inside_it_sides_included(self):
        self.assertEqual([(1, 1, 1)], geom.cells([[1, 1, 1], [1, 1, 1]]))
        self.assertEqual([(1, 1, 1), (3, 1, 1)], geom.cells([[1, 1, 1], [3, 1.5, 1]]))
        self.assertEqual([], geom.cells([[1.2, 1, 1], [2.8, 1, 1]]))

    def test_corners_in_any_order_and_as_points(self):
        self.assertEqual(geom.cells([[0, 0, 0], [4, 2, 4]]),
                         geom.cells([{"x": 4, "y": 0, "z": 4}, (0, 2, 0)]))

    def test_order_is_y_then_z_then_x(self):
        self.assertEqual([(1, 1, 1), (3, 1, 1), (1, 1, 3), (3, 1, 3), (1, 3, 1), (3, 3, 1), (1, 3, 3), (3, 3, 3)],
                         geom.cells([[0, 0, 0], [4, 4, 4]]))

    def test_small_cells_are_centred_on_half_metres(self):
        self.assertEqual([(0, 0, 0), (0.5, 0, 0), (1, 0, 0)], geom.cells([[0, 0, 0], [1, 0, 0]], size=0.5))
        self.assertEqual([(0.5, 0, 0)], geom.cells([[0.25, -0.25, -0.25], [0.75, 0.25, 0.25]], size=0.5))

    def test_another_size_is_refused(self):
        with self.assertRaises(ValueError):
            geom.cells([[0, 0, 0], [4, 4, 4]], size=1)

    def test_it_is_sg_geom(self):
        self.assertIs(geom, sg.geom)
        self.assertIn("geom", sg.__all__)


class MergeTests(unittest.TestCase):
    def test_a_row_merges_into_one_box(self):
        self.assertEqual([[[0, 0, 0], [6, 2, 2]]], geom.merge_rects([(1, 1, 1), (3, 1, 1), (5, 1, 1)]))

    def test_x_then_z_then_y(self):
        block = geom.cells([[0, 0, 0], [4, 4, 4]])
        self.assertEqual([[[0, 0, 0], [4, 4, 4]]], geom.merge_rects(block))
        # An L in one layer: the first row grows along x, the rest of the L is its own box.
        self.assertEqual([[[0, 0, 0], [4, 2, 2]], [[0, 0, 2], [2, 2, 4]]],
                         geom.merge_rects([(1, 1, 1), (3, 1, 1), (1, 1, 3)]))

    def test_small_grid_and_repeats(self):
        self.assertEqual([[[-0.25, -0.25, -0.25], [0.75, 0.25, 0.25]]],
                         geom.merge_rects([(0, 0, 0), (0.5, 0, 0), {"x": 0.5, "y": 0, "z": 0}], size=0.5))

    def test_no_cells_no_boxes(self):
        self.assertEqual([], geom.merge_rects([]))

    def test_random_cell_sets_are_covered_exactly(self):
        rng = random.Random(SEED)
        for size, offset in ((2, 1), (0.5, 0)):
            for trial in range(150):
                span = rng.randint(1, 6)
                density = rng.random()
                wanted = {(offset + x * size, offset + y * size, offset + z * size)
                          for x in range(-span, span) for y in range(-2, 2) for z in range(-span, span)
                          if rng.random() < density}
                with self.subTest(size=size, trial=trial):
                    boxes = geom.merge_rects(wanted, size)
                    covered = cells_of(boxes, size)
                    self.assertEqual(len(covered), len(set(covered)), "a cell in two boxes")
                    self.assertEqual(wanted, {tuple(float(v) for v in cell) for cell in covered})


class TilesTests(unittest.TestCase):
    def test_a_box_within_the_limit_is_one_tile(self):
        self.assertEqual([[[0, 0, 0], [4, 2, 4]]], geom.tiles([[0, 0, 0], [4, 2, 4]], 4))

    def test_tiles_are_even_and_hold_at_most_max_cells(self):
        boxes = geom.tiles([[0, 0, 0], [20, 2, 20]], 25)
        self.assertEqual(5, len(boxes))
        self.assertEqual([20] * 5, [len(geom.cells(box)) for box in boxes])

    def test_a_box_with_no_cell_has_no_tile(self):
        self.assertEqual([], geom.tiles([[1.2, 1, 1], [2.8, 1, 1]], 10))

    def test_max_cells_must_be_a_whole_number_of_at_least_one(self):
        for bad in (0, -1, 2.5, True):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                geom.tiles([[0, 0, 0], [2, 2, 2]], bad)

    def test_random_boxes_are_tiled_exactly(self):
        rng = random.Random(SEED + 1)
        for size in (2, 0.5):
            for trial in range(150):
                a = [rng.uniform(-30, 30) * size for _ in range(3)]
                b = [value + rng.uniform(-12, 12) * size for value in a]
                most = rng.randint(1, 60)
                with self.subTest(size=size, trial=trial, a=a, b=b, most=most):
                    wanted = geom.cells([a, b], size)
                    boxes = geom.tiles([a, b], most, size)
                    covered = cells_of(boxes, size)
                    self.assertTrue(all(len(geom.cells(box, size)) <= most for box in boxes))
                    self.assertEqual(len(covered), len(set(covered)), "a cell in two tiles")
                    self.assertEqual(set(wanted), set(covered))


if __name__ == "__main__":
    unittest.main()
