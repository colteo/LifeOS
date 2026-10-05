using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionTargetPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nutrition_target_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    default_calories_kcal = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    default_protein_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    default_carbs_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    default_fat_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_target_plans", x => x.id);
                    table.CheckConstraint("ck_nutrition_target_plans_default", "(default_calories_kcal IS NULL OR (default_calories_kcal > 0 AND default_calories_kcal <= 10000)) AND (default_protein_grams IS NULL OR (default_protein_grams > 0 AND default_protein_grams <= 1000)) AND (default_carbs_grams IS NULL OR (default_carbs_grams > 0 AND default_carbs_grams <= 1000)) AND (default_fat_grams IS NULL OR (default_fat_grams > 0 AND default_fat_grams <= 1000))");
                    table.CheckConstraint("ck_nutrition_target_plans_period", "starts_on >= DATE '0001-01-02' AND ends_on <= DATE '9999-12-30' AND ends_on >= starts_on");
                    table.ForeignKey(
                        name: "FK_nutrition_target_plans_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nutrition_target_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    calories_kcal = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    protein_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    carbs_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    fat_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_target_overrides", x => x.id);
                    table.CheckConstraint("ck_nutrition_target_overrides_mode", "mode IN ('Custom', 'NoTarget')");
                    table.CheckConstraint("ck_nutrition_target_overrides_values", "(calories_kcal IS NULL OR (calories_kcal > 0 AND calories_kcal <= 10000)) AND (protein_grams IS NULL OR (protein_grams > 0 AND protein_grams <= 1000)) AND (carbs_grams IS NULL OR (carbs_grams > 0 AND carbs_grams <= 1000)) AND (fat_grams IS NULL OR (fat_grams > 0 AND fat_grams <= 1000)) AND ((mode = 'Custom') = (calories_kcal IS NOT NULL OR protein_grams IS NOT NULL OR carbs_grams IS NOT NULL OR fat_grams IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_nutrition_target_overrides_nutrition_target_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "nutrition_target_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nutrition_target_overrides_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nutrition_target_plan_day_rules",
                columns: table => new
                {
                    weekday = table.Column<short>(type: "smallint", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    calories_kcal = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    protein_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    carbs_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    fat_grams = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_target_plan_day_rules", x => new { x.plan_id, x.weekday });
                    table.CheckConstraint("ck_nutrition_target_plan_day_rules_mode", "mode IN ('Default', 'Custom', 'NoTarget')");
                    table.CheckConstraint("ck_nutrition_target_plan_day_rules_values", "(calories_kcal IS NULL OR (calories_kcal > 0 AND calories_kcal <= 10000)) AND (protein_grams IS NULL OR (protein_grams > 0 AND protein_grams <= 1000)) AND (carbs_grams IS NULL OR (carbs_grams > 0 AND carbs_grams <= 1000)) AND (fat_grams IS NULL OR (fat_grams > 0 AND fat_grams <= 1000)) AND ((mode = 'Custom') = (calories_kcal IS NOT NULL OR protein_grams IS NOT NULL OR carbs_grams IS NOT NULL OR fat_grams IS NOT NULL))");
                    table.CheckConstraint("ck_nutrition_target_plan_day_rules_weekday", "weekday BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_nutrition_target_plan_day_rules_plan_id",
                        column: x => x.plan_id,
                        principalTable: "nutrition_target_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nutrition_target_overrides_plan_id",
                table: "nutrition_target_overrides",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ux_nutrition_target_overrides_user_date",
                table: "nutrition_target_overrides",
                columns: new[] { "user_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_target_plans_user_starts_on",
                table: "nutrition_target_plans",
                columns: new[] { "user_id", "starts_on" });

            // NUT-003 overlap guarantee (hand-written; not part of the EF model). A btree_gist exclusion
            // constraint would need CREATE EXTENSION, which the production migration role cannot run and
            // the runbook forbids. Instead every insert, and every update of a plan's period, takes a
            // transaction-scoped advisory lock for the user and then looks for an overlapping plan. In
            // READ COMMITTED each query of this (volatile) function takes a fresh snapshot, so after the
            // lock it sees any concurrent plan that committed first: two overlapping plans can never both
            // commit. Other isolation levels are refused rather than weakening that guarantee.
            migrationBuilder.Sql("""
                CREATE FUNCTION nutrition_target_plans_prevent_overlap() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                    conflicting uuid;
                BEGIN
                    IF current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'nutrition_target_plans writes require READ COMMITTED isolation'
                            USING ERRCODE = 'invalid_transaction_state';
                    END IF;

                    PERFORM pg_advisory_xact_lock(hashtextextended('nutrition_target_plans:' || NEW.user_id::text, 0));

                    SELECT plan.id INTO conflicting
                    FROM nutrition_target_plans AS plan
                    WHERE plan.user_id = NEW.user_id
                      AND plan.id <> NEW.id
                      AND plan.starts_on <= NEW.ends_on
                      AND NEW.starts_on <= plan.ends_on
                    LIMIT 1;

                    IF FOUND THEN
                        RAISE EXCEPTION 'Nutrition target plan % overlaps plan %', NEW.id, conflicting
                            USING ERRCODE = 'exclusion_violation',
                                  CONSTRAINT = 'ex_nutrition_target_plans_no_overlap',
                                  TABLE = 'nutrition_target_plans';
                    END IF;

                    RETURN NEW;
                END;
                $fn$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_nutrition_target_plans_no_overlap
                BEFORE INSERT OR UPDATE OF user_id, starts_on, ends_on ON nutrition_target_plans
                FOR EACH ROW EXECUTE FUNCTION nutrition_target_plans_prevent_overlap();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nutrition_target_overrides");

            migrationBuilder.DropTable(
                name: "nutrition_target_plan_day_rules");

            migrationBuilder.DropTable(
                name: "nutrition_target_plans");

            // The trigger went with the table.
            migrationBuilder.Sql("DROP FUNCTION nutrition_target_plans_prevent_overlap();");
        }
    }
}
