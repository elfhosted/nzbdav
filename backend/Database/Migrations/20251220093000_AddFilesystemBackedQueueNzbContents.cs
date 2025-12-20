using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesystemBackedQueueNzbContents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalCompression",
                table: "QueueNzbContents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExternalLengthBytes",
                table: "QueueNzbContents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPath",
                table: "QueueNzbContents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSha256",
                table: "QueueNzbContents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalCompression",
                table: "QueueNzbContents");

            migrationBuilder.DropColumn(
                name: "ExternalLengthBytes",
                table: "QueueNzbContents");

            migrationBuilder.DropColumn(
                name: "ExternalPath",
                table: "QueueNzbContents");

            migrationBuilder.DropColumn(
                name: "ExternalSha256",
                table: "QueueNzbContents");
        }
    }
}
