SELECT
    "result_order"."candidate_Preference" AS "candidate_Preference",
    $1 AS "candidate_Preference_present",
    "result_order"."candidate_Id" AS "candidate_Id",
    $2 AS "candidate_Id_present",
    "result_order"."candidate_Key" AS "candidate_Key",
    "result_order"."candidate_Key_present" AS "candidate_Key_present",
    "result_order"."candidate_Eligible" AS "candidate_Eligible",
    $3 AS "candidate_Eligible_present",
    ("result_order"."candidate_Id" IS NOT NULL) AS "candidate_binding_present",
    "result_order"."candidate_Id" AS "candidate_identity"
FROM (
    SELECT
        "eligible_winners"."candidate_Preference" AS "candidate_Preference",
        "eligible_winners"."candidate_Id" AS "candidate_Id",
        "eligible_winners"."candidate_Key" AS "candidate_Key",
        "eligible_winners"."candidate_Key_present" AS "candidate_Key_present",
        "eligible_winners"."candidate_Eligible" AS "candidate_Eligible"
    FROM (
        SELECT
            "representative"."candidate_Preference" AS "candidate_Preference",
            "representative"."candidate_Id" AS "candidate_Id",
            "representative"."candidate_Key" AS "candidate_Key",
            "representative"."candidate_Key_present" AS "candidate_Key_present",
            "representative"."candidate_Eligible" AS "candidate_Eligible"
        FROM (
            SELECT
                "representative_ranked"."candidate_Preference" AS "candidate_Preference",
                "representative_ranked"."candidate_Id" AS "candidate_Id",
                "representative_ranked"."candidate_Key" AS "candidate_Key",
                "representative_ranked"."candidate_Key_present" AS "candidate_Key_present",
                "representative_ranked"."candidate_Eligible" AS "candidate_Eligible"
            FROM (
                SELECT
                    "candidates"."candidate_Preference" AS "candidate_Preference",
                    "candidates"."candidate_Id" AS "candidate_Id",
                    "candidates"."candidate_Key" AS "candidate_Key",
                    "candidates"."candidate_Key_present" AS "candidate_Key_present",
                    "candidates"."candidate_Eligible" AS "candidate_Eligible",
                    ROW_NUMBER() OVER (
                        PARTITION BY "candidates"."candidate_Key_present", ("candidates"."candidate_Key" COLLATE "BINARY")
                        ORDER BY "candidates"."candidate_Preference" DESC NULLS LAST, "candidates"."candidate_Id" ASC NULLS LAST
                    ) AS "representative_rank"
                FROM (
                    SELECT
                        "candidate"."Preference" AS "candidate_Preference",
                        "candidate"."Id" AS "candidate_Id",
                        "candidate"."Key" AS "candidate_Key",
                        "candidate"."KeyPresent" AS "candidate_Key_present",
                        "candidate"."Eligible" AS "candidate_Eligible"
                    FROM "candidate" AS "candidate"
                ) AS "candidates"
            ) AS "representative_ranked"
            WHERE ("representative_ranked"."representative_rank" = $4)
        ) AS "representative"
        WHERE "representative"."candidate_Eligible"
    ) AS "eligible_winners"
    ORDER BY
        "eligible_winners"."candidate_Id" ASC NULLS LAST
) AS "result_order"
ORDER BY
    "result_order"."candidate_Id" ASC NULLS LAST
