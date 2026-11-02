using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Roadmap.Migrations
{
    /// <inheritdoc />
    public partial class PersonalRoadmapByChoice : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// 1.9 filled everyone's roadmap with every task assigned to them; from
        /// 1.10 a person adds what they want on it. The rows they never planned
        /// are the ones 1.9 put there for them, so those go. A row they dragged
        /// or resized carries their own days and stays.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM ext_roadmap."PersonalEntries" WHERE "StartAt" IS NULL;""");
        }

        /// <inheritdoc />
        /// <remarks>Nothing to put back: 1.9 re-adds what it wants on its next read.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
