using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ForgeLink.Contracts;
using ForgeLink.Web.Api;

namespace ForgeLink.IntegrationTests;

public sealed class ReportsTests(ForgeLinkFactory factory) : IClassFixture<ForgeLinkFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Rollup_then_daily_report_as_json_and_csv()
    {
        var client = factory.CreateClient();
        var run = await client.PostAsync("api/reports/oee/rollup", null);
        run.EnsureSuccessStatusCode();
        var result = await run.Content.ReadFromJsonAsync<KpiRollupResultDto>(Json);
        Assert.NotNull(result);
        Assert.True(result.RowsWritten > 0, "seeded shift history should produce machine-days");

        var rows = await client.GetFromJsonAsync<List<DailyOeeDto>>("api/reports/oee/daily?line=L1", Json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("L1", r.Line));
        Assert.Contains(rows, r => r.GoodCount > 0);

        var csv = await client.GetAsync("api/reports/oee/daily?line=L1&format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("oee-daily-L1-", csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("day,line,machine,", lines[0]);
        Assert.Equal(rows.Count + 1, lines.Length);
    }

    [Theory]
    [InlineData("JAM", "JAM")]
    [InlineData("A,B", "\"A,B\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-1", "'-1")]
    public void Csv_escapes_and_defuses_formulas(string input, string expected) => Assert.Equal(expected, Csv.Escape(input));
}
