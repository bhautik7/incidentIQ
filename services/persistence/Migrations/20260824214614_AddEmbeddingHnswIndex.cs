using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentIQ.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_ai_analyses_embedding_hnsw",
                table: "ai_analyses",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_analyses_embedding_hnsw",
                table: "ai_analyses");
        }
    }
}
