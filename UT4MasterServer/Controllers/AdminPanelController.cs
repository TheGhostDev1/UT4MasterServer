using Microsoft.AspNetCore.Mvc;
using UT4MasterServer.Authentication;
using UT4MasterServer.Common.Helpers;
using UT4MasterServer.Models.Database;
using UT4MasterServer.Models.DTO.Requests;
using UT4MasterServer.Common;
using UT4MasterServer.Services.Scoped;
using UT4MasterServer.Services.Singleton;
using UT4MasterServer.Models.DTO.Responses;
using UT4MasterServer.Models;
using UT4MasterServer.Models.Responses;
using Microsoft.Net.Http.Headers;
using UT4MasterServer.Common.Enums;

namespace UT4MasterServer.Controllers;

[ApiController]
[Route("admin")]
[AuthorizeBearer]
public sealed class AdminPanelController : ControllerBase
{
	private readonly ILogger<AdminPanelController> logger;
	private readonly AccountService accountService;
	private readonly SessionService sessionService;
	private readonly CodeService codeService;
	private readonly FriendService friendService;
	private readonly CloudStorageService cloudStorageService;
	private readonly StatisticsService statisticsService;
	private readonly ClientService clientService;
	private readonly TrustedGameServerService trustedGameServerService;
	private readonly RatingsService ratingsService;

	private readonly MatchmakingService matchmakingService;

	public AdminPanelController(
		ILogger<AdminPanelController> logger,
		AccountService accountService,
		SessionService sessionService,
		CodeService codeService,
		FriendService friendService,
		CloudStorageService cloudStorageService,
		StatisticsService statisticsService,
		ClientService clientService,
		TrustedGameServerService trustedGameServerService,
		RatingsService ratingsService,
		MatchmakingService matchmakingService)
	{
		this.logger = logger;
		this.accountService = accountService;
		this.sessionService = sessionService;
		this.codeService = codeService;
		this.friendService = friendService;
		this.cloudStorageService = cloudStorageService;
		this.statisticsService = statisticsService;
		this.clientService = clientService;
		this.trustedGameServerService = trustedGameServerService;
		this.ratingsService = ratingsService;
		this.matchmakingService = matchmakingService;
	}

	#region Accounts

	[HttpGet("flags")]
	public async Task<IActionResult> GetAllPossibleFlags()
	{
		await VerifyAccessAsync(AccountFlags.ACL_AccountsLow, AccountFlags.ACL_AccountsHigh);

		return Ok(Enum.GetNames<AccountFlags>().OrderBy(x => x));
	}

	[HttpGet("flags/{accountID}")]
	public async Task<IActionResult> GetAccountFlags(string accountID)
	{
		await VerifyAccessAsync(AccountFlags.ACL_AccountsLow, AccountFlags.ACL_AccountsHigh);

		AccountFlags? flags = await accountService.GetAccountFlagsAsync(EpicID.FromString(accountID));
		if (flags == null)
		{
			return NotFound();
		}

		List<string>? result = EnumHelpers.EnumToStrings(flags.Value);
		return Ok(result);
	}

	[HttpPut("flags/{accountID}")]
	public async Task<IActionResult> SetAccountFlags(string accountID, [FromBody] string[] flagNames)
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_AccountsLow, AccountFlags.ACL_AccountsHigh);

		List<AccountFlags>? flags = EnumHelpers.StringsToEnumArray<AccountFlags>(flagNames);

		Account? account = await accountService.GetAccountAsync(EpicID.FromString(accountID));
		if (account == null)
		{
			return NotFound();
		}

		List<AccountFlags>? flagsOld = EnumHelpers.EnumFlagsToEnumArray(account.Flags);
		AccountFlags adminFlags = admin.Account.Flags;

		AccountFlags flagsAdded = EnumHelpers.EnumArrayToEnumFlags(flags.Where(x => !flagsOld.Contains(x)));
		AccountFlags flagsRemoved = EnumHelpers.EnumArrayToEnumFlags(flagsOld.Where(x => !flags.Contains(x)));

		LogLevel logLevel = LogLevel.Warning;

		try
		{
			if (adminFlags.HasFlag(AccountFlags.Admin))
			{
				if (flagsRemoved.HasFlag(AccountFlags.Admin))
				{
					// TODO: there should be something like voting in order to pass this decision
					return Unauthorized($"Cannot remove {nameof(AccountFlags.Admin)} flag, this action must be performed with direct access to database");
				}

				if (flagsAdded.HasFlag(AccountFlags.Admin))
				{
					// TODO: there should be something like voting in order to pass this decision
				}
			}
			else if (adminFlags.HasFlag(AccountFlags.ACL_AccountsHigh))
			{
				if (flagsAdded.HasFlag(AccountFlags.Admin))
				{
					return Unauthorized($"Only {nameof(AccountFlags.Admin)} may add {nameof(AccountFlags.Admin)} flag to account");
				}

				if (flagsAdded.HasFlag(AccountFlags.ACL_AccountsHigh))
				{
					return Unauthorized($"Only {nameof(AccountFlags.Admin)} may add {nameof(AccountFlags.ACL_AccountsHigh)} flag to account");
				}

				if (flagsRemoved.HasFlag(AccountFlags.Admin))
				{
					return Unauthorized($"Cannot remove {nameof(AccountFlags.Admin)} flag");
				}

				if (AccountFlagsHelper.IsACLFlag(flagsRemoved))
				{
					return Unauthorized($"Only {nameof(AccountFlags.Admin)} may remove ACL flags");
				}
			}
			else // if (adminFlags.HasFlag(AccountFlags.ACL_AccountsLow))
			{
				if (AccountFlagsHelper.IsACLFlag(flagsAdded))
				{
					return Unauthorized($"Only {nameof(AccountFlags.Admin)} may add ACL flags");
				}

				if (AccountFlagsHelper.IsACLFlag(flagsRemoved))
				{
					return Unauthorized($"Only {nameof(AccountFlags.Admin)} may remove ACL flags");
				}
			}

			logLevel = LogLevel.Information;

			if (flagsRemoved.HasFlag(AccountFlags.HubOwner))
			{
				await trustedGameServerService.RemoveAllByAccountAsync(account.ID);
			}
			await accountService.UpdateAccountFlagsAsync(account.ID, EnumHelpers.EnumArrayToEnumFlags(flags));

			return Ok();
		}
		finally
		{
			logger.Log(
				logLevel,
				"{User} {OperationResultText} flags of {EditedUser}. | Added: {FlagsAdded} | Removed: {FlagsRemoved}",
				admin.Account,
				logLevel <= LogLevel.Information ? "edited" : "failed to edit",
				account,
				string.Join(", ", EnumHelpers.EnumToStrings(flagsAdded)),
				string.Join(", ", EnumHelpers.EnumToStrings(flagsRemoved))
			);
		}
	}

	[HttpPatch("change_password/{id}")]
	public async Task<IActionResult> ChangePassword(string id, [FromBody] AdminPanelChangePasswordRequest body)
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_AccountsLow, AccountFlags.ACL_AccountsHigh);

		Account? account = await accountService.GetAccountAsync(EpicID.FromString(id));
		if (account is null)
		{
			return NotFound(new ErrorResponse() { ErrorMessage = $"Failed to find account {id}" });
		}

		AccountFlags flags = account.Flags;
		LogLevel logLevel = LogLevel.Warning;

		try
		{
			if (admin.Account.Flags.HasFlag(AccountFlags.Admin))
			{
				if (flags.HasFlag(AccountFlags.Admin))
				{
					return Unauthorized($"Cannot change password of another {nameof(AccountFlags.Admin)}");
				}
			}
			else // if (admin.Account.Flags.HasFlag(AccountFlags.ACL_AccountsHigh) || adminFlags.HasFlag(AccountFlags.ACL_AccountsLow))
			{
				if (flags.HasFlag(AccountFlags.Admin))
				{
					return Unauthorized($"Cannot change password of {nameof(AccountFlags.Admin)} account");
				}

				if (AccountFlagsHelper.IsACLFlag(flags))
				{
					return Unauthorized("Cannot change password of an account with ACL flag");
				}
			}

			// passwords should already be hashed, but check its length just in case
			if (!ValidationHelper.ValidatePassword(body.NewPassword))
			{
				return BadRequest("newPassword is not a SHA512 hash");
			}

			if (account.Email != body.Email)
			{
				return BadRequest("Invalid email");
			}

			if (body.IAmSure != true)
			{
				return BadRequest("'iAmSure' was not 'true'");
			}

			await accountService.UpdateAccountPasswordAsync(account.ID, body.NewPassword);

			// logout user to make sure they remember the changed password by being forced to log in again,
			// as well as prevent anyone else from using this account after successful password change.
			await sessionService.RemoveSessionsWithFilterAsync(EpicID.Empty, account.ID, EpicID.Empty);

			return Ok();
		}
		finally
		{
			logger.Log(
				logLevel,
				"{User} {OperationResultText} password of {EditedUser}.",
				admin.Account,
				logLevel <= LogLevel.Information ? "changed" : "was not authorized to change",
				account
			);
		}
	}

	[HttpDelete("account/{id}")]
	public async Task<IActionResult> DeleteAccountInfo(string id, [FromBody] bool? forceCheckBroken)
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_AccountsHigh | AccountFlags.ACL_Maintenance);

		var accountID = EpicID.FromString(id);
		Account? account = await accountService.GetAccountUsernameAndFlagsAsync(accountID);

		LogLevel logLevel = LogLevel.Warning;
		try
		{
			if (account is null)
			{
				if (!admin.Account.Flags.HasFlag(AccountFlags.Admin) && !admin.Account.Flags.HasFlag(AccountFlags.ACL_Maintenance))
				{
					return NotFound(new ErrorResponse() { ErrorMessage = "Account not found" });
				}
			}
			else
			{
				if (admin.Account.Flags.HasFlag(AccountFlags.Admin))
				{
					if (account.Flags.HasFlag(AccountFlags.Admin))
					{
						return Unauthorized($"Cannot delete account of {nameof(AccountFlags.Admin)}. Account needs to be demoted first.");
					}
				}
				else if (admin.Account.Flags.HasFlag(AccountFlags.ACL_AccountsHigh))
				{
					if (account.Flags.HasFlag(AccountFlags.Admin))
					{
						return Unauthorized($"Cannot delete account of {nameof(AccountFlags.Admin)}");
					}

					if (AccountFlagsHelper.IsACLFlag(account.Flags))
					{
						return Unauthorized("Cannot delete account with ACL flag");
					}
				}
				else // if (admin.Account.Flags.HasFlag(AccountFlags.ACL_Maintenance))
				{
					return Unauthorized("You do not possess sufficient permissions to delete an existing account");
				}

				await accountService.RemoveAccountAsync(account.ID);
			}

			// remove all associated data
			await sessionService.RemoveSessionsWithFilterAsync(EpicID.Empty, accountID, EpicID.Empty);
			await codeService.RemoveAllByAccountAsync(accountID);
			await cloudStorageService.RemoveAllByAccountAsync(accountID);
			await statisticsService.RemoveAllByAccountAsync(accountID);
			await ratingsService.RemoveAllByAccountAsync(accountID);
			await friendService.RemoveAllByAccountAsync(accountID);
			await trustedGameServerService.RemoveAllByAccountAsync(accountID);
			// NOTE: missing removal of account from live servers. this should take care of itself in a relatively short time.

			logLevel = LogLevel.Information;

			return Ok();
		}
		finally
		{
			logger.Log(
				logLevel,
				"{User} {OperationResultText} account of {EditedUser}.",
				admin.Account,
				logLevel <= LogLevel.Information ? "deleted" : "was not authorized to delete",
				account
			);
		}
	}

	#endregion

	#region Clients

	[HttpPost("clients/new")]
	public async Task<IActionResult> CreateClient([FromBody] string name)
	{
		await VerifyAccessAsync(AccountFlags.ACL_Clients);

		var client = new Client(EpicID.GenerateNew(), EpicID.GenerateNew().ToString(), name);
		await clientService.UpdateAsync(client);

		return Ok(client);
	}

	[HttpGet("clients")]
	public async Task<IActionResult> GetAllClients()
	{
		await VerifyAccessAsync(AccountFlags.ACL_Clients);

		List<Client>? clients = await clientService.ListAsync();
		return Ok(clients);
	}

	[HttpGet("clients/{id}")]
	public async Task<IActionResult> GetClient(string id)
	{
		await VerifyAccessAsync(AccountFlags.ACL_Clients);

		Client? client = await clientService.GetAsync(EpicID.FromString(id));
		if (client == null)
		{
			return NotFound();
		}

		return Ok(client);
	}

	[HttpPatch("clients/{id}")]
	public async Task<IActionResult> UpdateClient(string id, [FromBody] Client client)
	{
		await VerifyAccessAsync(AccountFlags.ACL_Clients);

		var eid = EpicID.FromString(id);

		if (eid != client.ID)
		{
			return BadRequest();
		}

		if (IsSpecialClientID(eid))
		{
			return Forbid("Cannot modify reserved clients");
		}

		Task<bool?>? taskUpdateClient = clientService.UpdateAsync(client);
		Task? taskUpdateServerName = matchmakingService.UpdateServerNameAsync(client.ID, client.Name);

		await taskUpdateClient;
		await taskUpdateServerName;

		return Ok();
	}

	[HttpDelete("clients/{id}")]
	public async Task<IActionResult> DeleteClient(string id)
	{
		await VerifyAccessAsync(AccountFlags.ACL_Clients);

		var eid = EpicID.FromString(id);

		if (IsSpecialClientID(eid))
		{
			return Forbid("Cannot delete reserved clients");
		}

		var success = await clientService.RemoveAsync(eid);
		if (success != true)
		{
			return BadRequest();
		}

		// in case this client is a trusted server remove, it as well
		await trustedGameServerService.RemoveAsync(eid);

		return Ok();
	}

	#endregion

	#region Trusted Servers

	[HttpGet("trusted_servers")]
	public async Task<IActionResult> GetAllTrustedServers()
	{
		await VerifyAccessAsync(AccountFlags.ACL_TrustedServers);

		List<TrustedGameServer>? trustedServers = await trustedGameServerService.ListAsync();

		IEnumerable<EpicID>? trustedServerIDs = trustedServers.Select(t => t.ID);
		IEnumerable<EpicID>? trustedServerOwnerIDs = trustedServers.Select(t => t.OwnerID).Distinct();

		Task<List<Client>>? taskClients = clientService.GetManyAsync(trustedServerIDs);
		Task<IEnumerable<Account>>? taskAccounts = accountService.GetAccountsAsync(trustedServerOwnerIDs);

		List<Client>? clients = await taskClients;
		IEnumerable<Account>? accounts = await taskAccounts;

		IEnumerable<TrustedGameServerResponse>? response = trustedServers.Select(t => new TrustedGameServerResponse
		{
			ID = t.ID,
			OwnerID = t.OwnerID,
			TrustLevel = t.TrustLevel,
			Client = clients.SingleOrDefault(c => c.ID == t.ID),
			Owner = accounts.SingleOrDefault(a => a.ID == t.OwnerID)
		});
		return Ok(response);
	}

	[HttpGet("trusted_servers/{id}")]
	public async Task<IActionResult> GetTrustedServer(string id)
	{
		await VerifyAccessAsync(AccountFlags.ACL_TrustedServers);

		TrustedGameServer? ret = await trustedGameServerService.GetAsync(EpicID.FromString(id));
		return Ok(ret);
	}

	[HttpPost("trusted_servers")]
	public async Task<IActionResult> CreateTrustedServer([FromBody] TrustedGameServer body)
	{
		await VerifyAccessAsync(AccountFlags.ACL_TrustedServers);

		Client? client = await clientService.GetAsync(body.ID);
		if (client is null)
		{
			return BadRequest("Trusted server does not have a matching client with same ID");
		}

		TrustedGameServer? server = await trustedGameServerService.GetAsync(body.ID);
		if (server is not null)
		{
			return BadRequest($"Trusted server {body.ID} already exists");
		}

		Account? owner = await accountService.GetAccountAsync(body.OwnerID);
		if (owner is null)
		{
			return BadRequest($"OwnerID {body.OwnerID} is not a valid account ID");
		}

		if (!owner.Flags.HasFlag(AccountFlags.HubOwner))
		{
			return BadRequest($"Account specified with OwnerID {body.OwnerID} is not marked as HubOwner");
		}

		await trustedGameServerService.UpdateAsync(body);
		await matchmakingService.UpdateTrustLevelAsync(body.ID, body.TrustLevel);

		return Ok();
	}

	[HttpPatch("trusted_servers/{id}")]
	public async Task<IActionResult> UpdateTrustedServer(string id, [FromBody] TrustedGameServer server)
	{
		await VerifyAccessAsync(AccountFlags.ACL_TrustedServers);

		var eid = EpicID.FromString(id);

		if (eid != server.ID)
		{
			return BadRequest();
		}

		await trustedGameServerService.UpdateAsync(server);
		await matchmakingService.UpdateTrustLevelAsync(server.ID, server.TrustLevel);

		return Ok();
	}

	[HttpDelete("trusted_servers/{id}")]
	public async Task<IActionResult> DeleteTrustedServer(string id)
	{
		await VerifyAccessAsync(AccountFlags.ACL_TrustedServers);

		var eid = EpicID.FromString(id);
		var ret = await trustedGameServerService.RemoveAsync(eid);

		await matchmakingService.UpdateTrustLevelAsync(eid, GameServerTrust.Untrusted);

		return Ok(ret);
	}

	#endregion

	#region Cloud Storage

	[HttpGet("mcp_files")]
	public async Task<IActionResult> GetMCPFiles()
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_CloudStorageAnnouncements | AccountFlags.ACL_CloudStorageRulesets | AccountFlags.ACL_CloudStorageChallenges);
		List<CloudFile>? files = await cloudStorageService.ListFilesAsync(EpicID.Empty, false);
		IEnumerable<CloudFileAdminPanelResponse>? responseFiles = files.Select(x => new CloudFileAdminPanelResponse(x, IsAccessibleCloudStorageFile(admin.Account.Flags, x.Filename)));
		return Ok(responseFiles);
	}

	[HttpPost("mcp_files")]
	public async Task<IActionResult> UpdateMCPFile()
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_CloudStorageAnnouncements | AccountFlags.ACL_CloudStorageRulesets | AccountFlags.ACL_CloudStorageChallenges);

		IFormCollection? formCollection = await Request.ReadFormAsync();
		if (formCollection.Files.Count < 1)
		{
			return BadRequest("Missing file");
		}

		IFormFile? file = formCollection.Files[0];
		var filename = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.ToString().Trim('"');
		if (file.Length <= 0)
		{
			return BadRequest("Cannot upload empty file");
		}

		if (!IsAccessibleCloudStorageFile(admin.Account.Flags, filename))
		{
			return Unauthorized("You are not authorized to upload this file");
		}

		using (Stream? stream = file.OpenReadStream())
		{
			await cloudStorageService.UpdateFileAsync(EpicID.Empty, filename, stream);
		}
		return Ok();
	}

	[HttpGet("mcp_files/{filename}"), Produces("application/octet-stream")]
	public async Task<IActionResult> GetMCPFile(string filename)
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_CloudStorageAnnouncements | AccountFlags.ACL_CloudStorageRulesets | AccountFlags.ACL_CloudStorageChallenges);

		CloudFile? file = await cloudStorageService.GetFileAsync(EpicID.Empty, filename);
		if (file is null)
		{
			return NotFound(new ErrorResponse() { ErrorMessage = "File not found" });
		}

		return new FileContentResult(file.RawContent, "application/octet-stream");
	}

	[HttpDelete("mcp_files/{filename}")]
	public async Task<IActionResult> DeleteMCPFile(string filename)
	{
		(Session Session, Account Account) admin = await VerifyAccessAsync(AccountFlags.ACL_CloudStorageAnnouncements | AccountFlags.ACL_CloudStorageRulesets | AccountFlags.ACL_CloudStorageChallenges);

		if (!IsAccessibleCloudStorageFile(admin.Account.Flags, filename))
		{
			return Unauthorized("You are not authorized to delete this file");
		}

		if (await cloudStorageService.DeleteFileAsync(EpicID.Empty, filename) != true)
		{
			return Forbid("Cannot delete file. Either this file is not deletable or something went wrong.");
		}
		return Ok();
	}

	#endregion

	[NonAction]
	private async Task<(Session Session, Account Account)> VerifyAccessAsync(params AccountFlags[] aclAny)
	{
		if (User.Identity is not EpicUserIdentity user)
		{
			throw new UnauthorizedAccessException("User not logged in");
		}

		Account? account = await accountService.GetAccountAsync(user.Session.AccountID);
		if (account == null)
		{
			throw new UnauthorizedAccessException("User not found");
		}

		AccountFlags combinedAcl = aclAny.Aggregate((x, y) => x | y) | AccountFlags.Admin;

		if (!account.Flags.HasFlagAny(combinedAcl))
		{
			throw new UnauthorizedAccessException("User has insufficient privileges");
		}

		return (user.Session, account);
	}

	[NonAction]
	private static bool IsSpecialClientID(EpicID id)
	{
		if (id == ClientIdentification.Game.ID || id == ClientIdentification.ServerInstance.ID || id == ClientIdentification.Launcher.ID)
		{
			return true;
		}

		return false;
	}

	[NonAction]
	private static bool IsAccessibleCloudStorageFile(AccountFlags flags, string filename)
	{
		if (flags.HasFlag(AccountFlags.Admin))
		{
			return true;
		}
		if (flags.HasFlag(AccountFlags.ACL_CloudStorageAnnouncements))
		{
			if (filename == "UnrealTournmentMCPAnnouncement.json" || filename.StartsWith("news-"))
			{
				return true;
			}
		}
		if (flags.HasFlag(AccountFlags.ACL_CloudStorageRulesets))
		{
			var allowedFilenames = new[] { "UTMCPPlaylists.json", "UnrealTournamentOnlineSettings.json", "UnrealTournmentMCPGameRulesets.json" };
			if (allowedFilenames.Contains(filename))
			{
				return true;
			}
		}
		if (flags.HasFlag(AccountFlags.ACL_CloudStorageChallenges))
		{
			if (filename == "UnrealTournmentMCPStorage.json")
			{
				return true;
			}
		}

		return false;
	}
}
