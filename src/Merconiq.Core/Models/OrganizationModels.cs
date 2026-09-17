namespace Merconiq.Core.Models;

public sealed record CreateCompanyRequest(
    string Code,
    string LegalName,
    string? TradingName,
    string? RegistrationNumber,
    string? TaxIdentifier,
    string BaseCurrency,
    string? CountryCode,
    int? CurrencyScale = null);

public sealed record UpdateCompanyRequest(
    string LegalName,
    string? TradingName,
    string? RegistrationNumber,
    string? TaxIdentifier,
    string BaseCurrency,
    string? CountryCode,
    bool IsActive,
    int? CurrencyScale = null);

public sealed record CreateBranchRequest(
    int CompanyId,
    string Code,
    string Name,
    string? Address,
    string TimeZoneId);

public sealed record UpdateBranchRequest(
    string Name,
    string? Address,
    string TimeZoneId,
    bool IsActive);

public sealed record AssignLocationBranchRequest(int BranchId);
