#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Util;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// dish_aim: the Horizontal and Vertical (logic degrees) that point a satellite dish at a contact, found on the dish's
/// own model. Read only: the dish does not move; write the returned values to its Horizontal and Vertical to turn it.
///
/// The angles reach the model only through its Animator: SatelliteDish's Horizontal and Vertical setters pass the
/// 0..1 ratios to BaseAnimator.SetFloat, then read DishForward = DishTransform.up. So this poses the Animator at trial
/// ratios, evaluates it with Animator.Update(0) and reads DishTransform.up: a coarse grid over the whole range, then a
/// shrinking-step search (Pure.AngleSearch.DishMinimum). All of it runs inside one main-thread call and the dish's own
/// ratios are restored and evaluated again before returning, so no frame renders a trial pose and nothing is left
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

        Animator animator = dish.BaseAnimator;
        Transform pointer = dish.DishTransform;
        if (animator == null || pointer == null)
        {
            throw ApiErrors.Refused("dish_not_ready", "The dish has no animator or pointing transform.");
        }

        DishPoser poser = new DishPoser(animator, pointer, contact.Angle.normalized);
        try
        {
            return Aim(dish, poser, dishId, contactId);
        }
        finally
        {
            poser.Restore();
        }
    }

    private static DishAimView Aim(SatelliteDish dish, DishPoser poser, ThingId dishId, ThingId contactId)
    {
        Vector3 before = poser.Pointer.up;
        float staleDeg = Vector3.Angle(before, poser.Pose(poser.OriginalHorizontal, poser.OriginalVertical));
        if (Vector3.Angle(poser.Pose(0f, 0f), poser.Pose(0.5f, 1f)) < MinimumTravelDeg)
        {
            throw ApiErrors.Refused("dish_not_ready", "The dish's animator does not move its model when evaluated.");
        }

        Pure.AngleBest found = Pure.AngleSearch.DishMinimum(poser);
        Vector3 aimed = poser.Pose(found.Horizontal, found.Vertical);
        Vector3 target = poser.Target;
        DishAimResult result = new DishAimResult(found.Horizontal * dish.MaximumHorizontal,
            found.Vertical * dish.MaximumVertical, found.Score, Contacts.Vector(aimed), Contacts.Vector(target));
        Vector3 forward = dish.DishForward;
        DishNowView current = new DishNowView(dish.GetLogicValue(LogicType.Horizontal),
            dish.GetLogicValue(LogicType.Vertical), Contacts.Vector(forward),
            forward.sqrMagnitude > SetForwardSqr ? Vector3.Angle(forward, target) : null);
        return new DishAimView(dishId, contactId, result, current, staleDeg, poser.Samples);
    }
}

/// <summary>Poses a dish's Animator at trial ratios and scores where its model points against a target.</summary>
internal sealed class DishPoser : Pure.IAngleScore
{
    private readonly Animator _animator;
    private readonly int _horizontalHash = Defines.Animator.Horizontal;
    private readonly int _verticalHash = Defines.Animator.Vertical;

    internal DishPoser(Animator animator, Transform pointer, Vector3 target)
    {
        _animator = animator;
        Pointer = pointer;
        Target = target;
        OriginalHorizontal = animator.GetFloat(_horizontalHash);
        OriginalVertical = animator.GetFloat(_verticalHash);
    }

    internal Transform Pointer { get; }

    internal Vector3 Target { get; }

    internal float OriginalHorizontal { get; }

    internal float OriginalVertical { get; }

    internal int Samples { get; private set; }

    /// <summary>Animator.SetFloat both ratios, Animator.Update(0), and read DishTransform.up.</summary>
    internal Vector3 Pose(float horizontal, float vertical)
    {
        _animator.SetFloat(_horizontalHash, horizontal);
        _animator.SetFloat(_verticalHash, vertical);
        _animator.Update(0f);
        Samples++;
        return Pointer.up;
    }

    public float Score(float horizontal, float vertical) => Vector3.Angle(Pose(horizontal, vertical), Target);

    internal void Restore()
    {
        _animator.SetFloat(_horizontalHash, OriginalHorizontal);
        _animator.SetFloat(_verticalHash, OriginalVertical);
        _animator.Update(0f);
    }
}
