#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Util;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// dish_aim: the Horizontal and Vertical (logic degrees) that point a satellite dish at a contact, found on the dish's
/// own model. Read only: the dish does not move; write the returned values to its Horizontal and Vertical to turn it.
///
/// The angles reach the model in one of two ways. SatelliteDish's Horizontal and Vertical setters pass the 0..1 ratios
/// to BaseAnimator.SetFloat, then read DishForward = DishTransform.up; SmallSatelliteDish has no Animator and its
/// setters call SetDishRotation, which sets _horizontalPivot.localRotation = Euler(0, Lerp(-90, 270, h), 0) and
/// DishTransform.localRotation = Euler(0, 0, Lerp(0, 90, v)). So this poses the model at trial ratios the same way
/// (Animator.SetFloat and Animator.Update(0), or the two local rotations) and reads DishTransform.up: a coarse grid over
/// the whole range, then a shrinking-step search (Pure.AngleSearch.DishMinimum). All of it runs inside one main-thread
/// call and the dish's own pose is restored before returning, so no frame renders a trial pose and nothing is left
/// changed.
/// </summary>
internal static class DishAimApi
{
    // A dish whose model turns less than this between two far-apart poses is not being evaluated.
    private const float MinimumTravelDeg = 1f;

    // DishForward counts as set once it is at least this long (squared).
    private const float SetForwardSqr = 0.25f;

    internal static DishAimView Handle(Args args)
    {
        ThingId dishId = args.ThingId("dish_id");
        ThingId contactId = args.ThingId("contact_id");
        if (!(Thing.Find(dishId.Value) is SatelliteDish dish) || dish == null)
        {
            throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"No satellite dish has reference id {dishId}.");
        }

        TraderContact? contact = Contacts.Find(contactId.Value);
        if (contact == null)
        {
            throw ApiErrors.Refused("contact_not_found", $"No trader contact has reference id {contactId}.");
        }

        DishPoser poser = PoserFor(dish, contact.Angle.normalized);
        try
        {
            return Aim(dish, poser, dishId, contactId);
        }
        finally
        {
            poser.Restore();
        }
    }

    // The Small dish turns its pivots directly; every other dish through its Animator.
    private static DishPoser PoserFor(SatelliteDish dish, Vector3 target)
    {
        Transform pointer = dish.DishTransform;
        if (dish is SmallSatelliteDish small)
        {
            Transform? pivot = GameMembers.SmallDishHorizontalPivot.GetValue(small) as Transform;
            if (pointer == null || pivot == null)
            {
                throw ApiErrors.Refused("dish_not_ready", "The dish has no horizontal pivot or pointing transform.");
            }

            return new PivotDishPoser(pivot, pointer, target, (float)small.Horizontal, (float)small.Vertical);
        }

        Animator animator = dish.BaseAnimator;
        if (animator == null || pointer == null)
        {
            throw ApiErrors.Refused("dish_not_ready", "The dish has no animator or pointing transform.");
        }

        return new AnimatorDishPoser(animator, pointer, target);
    }

    private static DishAimView Aim(SatelliteDish dish, DishPoser poser, ThingId dishId, ThingId contactId)
    {
        Vector3 before = poser.Pointer.up;
        float staleDeg = DishPoser.AngleDeg(before, poser.Pose(poser.OriginalHorizontal, poser.OriginalVertical));
        if (DishPoser.AngleDeg(poser.Pose(0f, 0f), poser.Pose(0.5f, 1f)) < MinimumTravelDeg)
        {
            throw ApiErrors.Refused("dish_not_ready", "The dish's model does not move when posed.");
        }

        Pure.AngleBest found = Pure.AngleSearch.DishMinimum(poser);
        Vector3 aimed = poser.Pose(found.Horizontal, found.Vertical);
        Vector3 target = poser.Target;
        DishAimResult result = new DishAimResult(found.Horizontal * dish.MaximumHorizontal,
            found.Vertical * dish.MaximumVertical, found.Score, Contacts.Vector(aimed), Contacts.Vector(target));
        Vector3 forward = dish.DishForward;
        DishNowView current = new DishNowView(dish.GetLogicValue(LogicType.Horizontal),
            dish.GetLogicValue(LogicType.Vertical), Contacts.Vector(forward),
            forward.sqrMagnitude > SetForwardSqr ? DishPoser.AngleDeg(forward, target) : null);
        return new DishAimView(dishId, contactId, Contacts.Readiness(dish), result, current, staleDeg,
            poser.Samples);
    }
}

/// <summary>Poses a dish's model at trial ratios and scores where it points against a target.</summary>
internal abstract class DishPoser : Pure.IAngleScore
{
    private protected DishPoser(Transform pointer, Vector3 target, float originalHorizontal, float originalVertical)
    {
        Pointer = pointer;
        Target = target;
        OriginalHorizontal = originalHorizontal;
        OriginalVertical = originalVertical;
    }

    internal Transform Pointer { get; }

    internal Vector3 Target { get; }

    internal float OriginalHorizontal { get; }

    internal float OriginalVertical { get; }

    internal int Samples { get; private set; }

    /// <summary>Poses the model at both ratios and reads DishTransform.up.</summary>
    internal Vector3 Pose(float horizontal, float vertical)
    {
        Apply(horizontal, vertical);
        Samples++;
        return Pointer.up;
    }

    public float Score(float horizontal, float vertical) => AngleDeg(Pose(horizontal, vertical), Target);

    /// <summary>Degrees between two directions in double (Pure.VectorAngle), so a miss under 0.03 is kept.</summary>
    internal static float AngleDeg(Vector3 a, Vector3 b) =>
        (float)Pure.VectorAngle.Degrees(a.x, a.y, a.z, b.x, b.y, b.z);

    /// <summary>Puts the model back as it was before the first pose.</summary>
    internal abstract void Restore();

    private protected abstract void Apply(float horizontal, float vertical);
}

/// <summary>A dish turned by its Animator: SetFloat both ratios, then Animator.Update(0).</summary>
internal sealed class AnimatorDishPoser : DishPoser
{
    private static readonly int HorizontalHash = Defines.Animator.Horizontal;
    private static readonly int VerticalHash = Defines.Animator.Vertical;

    private readonly Animator _animator;

    internal AnimatorDishPoser(Animator animator, Transform pointer, Vector3 target)
        : base(pointer, target, animator.GetFloat(HorizontalHash), animator.GetFloat(VerticalHash))
    {
        _animator = animator;
    }

    internal override void Restore() => Apply(OriginalHorizontal, OriginalVertical);

    private protected override void Apply(float horizontal, float vertical)
    {
        _animator.SetFloat(HorizontalHash, horizontal);
        _animator.SetFloat(VerticalHash, vertical);
        _animator.Update(0f);
    }
}

/// <summary>
/// The Small dish, turned as its SetDishRotation does: the horizontal pivot's local yaw Lerp(-90, 270, h) and the
/// pointing transform's local roll Lerp(0, 90, v). Restore puts both local rotations back exactly.
/// </summary>
internal sealed class PivotDishPoser : DishPoser
{
    private readonly Transform _pivot;
    private readonly Quaternion _pivotBefore;
    private readonly Quaternion _pointerBefore;

    internal PivotDishPoser(Transform pivot, Transform pointer, Vector3 target, float horizontal, float vertical)
        : base(pointer, target, horizontal, vertical)
    {
        _pivot = pivot;
        _pivotBefore = pivot.localRotation;
        _pointerBefore = pointer.localRotation;
    }

    internal override void Restore()
    {
        _pivot.localRotation = _pivotBefore;
        Pointer.localRotation = _pointerBefore;
    }

    private protected override void Apply(float horizontal, float vertical)
    {
        _pivot.localRotation = Quaternion.Euler(0f, Mathf.Lerp(-90f, 270f, horizontal), 0f);
        Pointer.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(0f, 90f, vertical));
    }
}
