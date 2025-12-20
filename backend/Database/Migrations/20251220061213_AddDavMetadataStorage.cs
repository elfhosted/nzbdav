using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDavMetadataStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RarParts",
                table: "DavRarFiles",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "MetadataStorageHash",
                table: "DavRarFiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SegmentIds",
                table: "DavNzbFiles",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "MetadataStorageHash",
                table: "DavNzbFiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "DavMultipartFiles",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "MetadataStorageHash",
                table: "DavMultipartFiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataStorageHash",
                table: "DavRarFiles");

            migrationBuilder.DropColumn(
                name: "MetadataStorageHash",
                table: "DavNzbFiles");

            migrationBuilder.DropColumn(
                name: "MetadataStorageHash",
                table: "DavMultipartFiles");

            migrationBuilder.AlterColumn<string>(
                name: "RarParts",
                table: "DavRarFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SegmentIds",
                table: "DavNzbFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "DavMultipartFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
