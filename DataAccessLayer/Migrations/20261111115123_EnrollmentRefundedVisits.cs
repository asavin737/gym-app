using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentRefundedVisits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("UPDATE enrollments SET refunded_visits = 0 WHERE status = 'cancelled';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refunded_visits",
                table: "enrollments");
        }
    }
}
