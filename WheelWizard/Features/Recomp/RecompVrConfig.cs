namespace WheelWizard.Recomp;

public static class RecompVrConfig
{
    /// <summary>
    /// The <c>[vr]</c> keys "Reset VR settings" removes, so the game reads its own defaults for them:
    /// a copy of WiiCompiled VR's <c>kVrPreferenceKeys</c> (runtime/include/runtime_config.h), which the
    /// in-game panel and the Quest launcher remove too. It covers every VR and Camera setting, those
    /// only the in-game panel shows included, but not <c>enabled</c>, <c>required</c>,
    /// <c>controller_mode</c>, <c>direct_presentation</c> or <c>multiview</c>.
    /// <c>frame_interpolation</c> is <c>frame_interpolation_fps</c>'s older spelling.
    /// </summary>
    public static IReadOnlyList<string> PreferenceKeys { get; } =
        [
            // VR
            "flat_screen",
            "immersive_window",
            "flat_race_intro",
            "render_scale",
            "performance_level",
            "foveation",
            "frame_interpolation_fps",
            "frame_interpolation",
            "refresh_rate",
            "passthrough",
            "mirror_view",
            "recenter_key",
            "hud_virtual_screen",
            "hud_distance_meters",
            "hud_width_meters",
            // Camera
            "lean_back_degrees",
            "first_person",
            "first_person_toggle_click",
            "bullet_bill_camera",
            "first_person_seat",
            "cockpit_units_per_meter",
            "first_person_units_per_meter",
            "first_person_head_up_meters",
            "first_person_head_forward_meters",
            "first_person_head_right_meters",
            "first_person_rotation",
            "body_ik",
            "body_ik_hands_only",
            "body_ik_left_hand_roll",
            "body_ik_right_hand_roll",
            "first_person_hide_driver",
            "first_person_hidden_model",
            "steering_wheel",
            "native_steering_wheel",
            "placeholder_steering_wheel",
            "hand_steering",
            "wheel_kart_degrees",
            "wheel_bike_degrees",
            "wheel_grab_distance",
            "wheel_grab_assist",
            "wheel_response",
            "wheel_tracking_grace",
            "wheel_haptics",
            "cockpit_item_hand",
            "cockpit_item_throw",
            "cockpit_item_size",
            "hand_tracking",
            "object_culling",
        ];
}
