using UnityEngine;
using AutoyaFramework.Connections.HTTP;
using AutoyaFramework.Representation;
using AutoyaFramework.Settings.Auth;

using System;
using System.Collections.Generic;
using UniRx;
using System.Text;

namespace AutoyaFramework {
	public partial class Autoya {
		/*
			authentication implementation.
				this feature controls the flow of authentication.


			0. only 2 data required for using authentication in Autoya.
				privateKey
				token


			privateKey:bytes
				data which is implemented in app and use for initial boot connection between client to server.
				

			token:JWT
				data for authentication, ahthorization and refreshing token.
				this will be stored in the app and use for authorization between client to server.
				initially this parameter is empty and should be expired by server.

				in initial boot, server returns this parameter if the access is valid.
					privateKey -> server generates token -> client get it and store it.
				

			1. Automatic-Authentination in Autoya is based on these scenerio: 
				"get and store token on initial boot, access with token, renew token, revoke token.".
				this is useful for player. player can play game without input anything.

				Autoya supports that basically.


				graph1: basic [APP STATE] and (LOGIN CONDITION)

					[init-boot] → [auth]						 (automatic-authenticated)
									↓
								[got token] → [use token]		 (logged-in)
									↑				↓
							[refresh token] ← [token expiration] (logged-out)


				on Initial Boot:
					client(privateKey) -> server -> (token) -> client(token)
						server automatically authenticate player, then returns token for new player.

				on Connection:
					client(token) -> server -> (response) -> client(token)
						client sends token to server for each connection(e.g. HTTP connection).
						server authenticate and authorize client by token.
						token will be expired.

				on Token Expired:
					client(token) -> server -> (auth error:token expired) -> client(expired-token)
						when token is expired in server, server returns auth error to client.
						client gets error.

				on Refresh Token:
					client(refresh-token) -> server -> (token) -> client(token)
						token can be refresh by refresh-token which is contained in expired-token.
						only latest refresh-token can be use.

				on Revoke Token:
					client(token) -> client()
						client can delete their own token. client will connect to server as "inital boot".
						this action recreates client's identity.
	
			
			2. Manual-Authorize-Extension in Autoya is based on these scenerio:
				Carryout: player wants to change their device, Autoya requires eMail and password or other provided Id token.
				Upgrade1: upgrading player's account with eMail and password.
				Upgrade2: upgrading player's account with 3rdParty id. (e.g. Twitter, Facebook, and other 3rd IdProvider.)[UNDER CONSTRUCTION]
				
				in these cases, Autoya can extends it's state and condition.
				actually, "Carryout" is motivated by "Upgrade1" or "Upgrade2".

				
				graph2: Extended [APP STATE] and (LOGIN CONDITION)

						→	→	[got token] → [use token] → [carryout]/[add mail+pass]/[add 3rdParty Identity]	(logged-in)
						↑			↑				↓								↓
						↑	[refresh token] ← [token expiration] 	←	←	← 	←	↓							(logged-out)
						↑															↓
						↑	←	←	←	← 	←	[manual login]	 	←	←	← [revoke token]					(logged-out by manual)
				

				on Carryout: player wants to change their device, Autoya requires eMail and password or other provided Id .
					client(token, mail, pass) -> server
						adding mail and pass to player-id on server.
						after that, player can be log-in with mail and pass.

				on Upgrade1: upgrading player's account with eMail and password.
					client(token, mail, pass) -> server
						adding mail and pass to player-id on server.
						after that, player can be log-in with mail and pass.

				on Upgrade2: upgrading player's account with 3rdParty id. (e.g. Twitter, Facebook, and other 3rd IdProvider.)
					client(token, 3rd data) -> server
						adding mail and pass to player-id on server.
						after that, player can be log-in with mail and pass.
						

			TODO:複数のハードポイントを吐き出せるはず。
			・idのsaveValidation
			・idのloadValidation

			・tokenのsaveValidation
			・tokenのloadValidation

			・tokenRequestのrequestHeaderValidation
			・tokenRequestのresponseHeaderValidation
		*/
		
		private string _token;
		private void InitializeTokenAuth () {
			_loginState = LoginState.LOGGED_OUT;
			
			/*
				set default handlers.
			*/
			OnLoginSucceeded = token => {
				LoggedIn(token);
			};

			OnAuthFailed = (conId, reason) => {
				LogOut();
				return false;
			};

			Login();
		}


		private int Progress () {
			return (int)_loginState;
		}


		private void LoggedIn (string newToken) {
			Debug.Assert(!(string.IsNullOrEmpty(newToken)), "token is null.");

			_token = newToken;
			_loginState = LoginState.LOGGED_IN;
		}

		private void LogOut () {
			_loginState = LoginState.LOGGED_OUT;
			Debug.LogWarning("登録ユーザーでないと、これを行うと死ぬ(ユーザー情報がないので復帰できなくなる)ので、簡易可視化idみたいなのがあるといい気がする。carryoutが影響受けそう");
			RevokeToken();
		}

		
		/**
			load token then login.
		*/
		private void Login () {
			var tokenCandidatePaths = _autoyaFilePersistence.FileNamesInDomain(BackyardSettings.AUTH_STORED_FRAMEWORK_DOMAIN);
			if (tokenCandidatePaths.Length == 0) {
				StartBootAccess();
				return;
			}

			var tokenCandidate = LoadToken();

			/*
				if token is already stored and valid, goes to login.
				else, get token then login.
			*/
			var isValid = IsTokenExist(tokenCandidate);
			
			if (isValid) {
				Debug.LogWarning("(maybe) valid token found. start login with it and get refresh token.");
				AttemptLoginByTokenCandidate(tokenCandidate);
			} else {
				Debug.LogWarning("no token found or token expired. if first boot, get token then login.");
				RefreshTokenThenLogin();
			}
		}

		private bool SaveToken (string newTokenCandidate) {
			return _autoyaFilePersistence.Update(
				BackyardSettings.AUTH_STORED_FRAMEWORK_DOMAIN, 
				BackyardSettings.AUTH_STORED_TOKEN_FILENAME,
				newTokenCandidate
			);
		}

		private string LoadToken () {
			return _autoyaFilePersistence.Load(
				BackyardSettings.AUTH_STORED_FRAMEWORK_DOMAIN, 
				BackyardSettings.AUTH_STORED_TOKEN_FILENAME
			);
		}

		private void StartBootAccess () {
			_loginState = LoginState.GETTING_TOKEN;
			
			var tokenUrl = AuthSettings.AUTH_URL_BOOT;
			
			/*
				create new http connection instance for boot access.
			*/
			var tokenHttp = new HTTPConnection();
			var tokenConnectionId = BackyardSettings.AUTH_CONNECTIONID_BOOT_PREFIX + Guid.NewGuid().ToString();
			
			var bootKeyword = AuthSettings.AUTH_BOOT;
			var authStr = Editable.EncryptionEditable.OnFirstBootRequestEncryption(bootKeyword);
			
			var tokenRequestHeaders = new Dictionary<string, string>{
				{"Auth", authStr}
			};
			
			Observable.FromCoroutine(
				() => tokenHttp.Get(
					tokenConnectionId,
					tokenRequestHeaders,
					tokenUrl,
					(conId, code, responseHeaders, data) => {
						EvaluateTokenResult(conId, responseHeaders, code, data);
					},
					(conId, code, failedReason, responseHeaders) => {
						EvaluateTokenResult(conId, responseHeaders, code, failedReason);
					}
				)
			).Timeout(
				TimeSpan.FromSeconds(BackyardSettings.HTTP_TIMEOUT_SEC)
			).Subscribe(
				_ => {},
				ex => {
					var errorType = ex.GetType();

					switch (errorType.ToString()) {
						case BackyardSettings.AUTH_HTTP_INTERNALERROR_TYPE_TIMEOUT: {
							EvaluateTokenResult(tokenConnectionId, new Dictionary<string, string>(), BackyardSettings.AUTH_HTTP_INTERNALERROR_CODE_TIMEOUT, "timeout:" + ex.ToString());
							break;
						}
						default: {
							Debug.LogError("failed to get token by undefined reason:" + ex.Message);
							// throw new Exception("failed to get token by undefined reason:" + ex.Message);
							break;
						}
					}
				}
			);
		}

		private void RefreshTokenThenLogin () {
			_loginState = LoginState.REFRESHING_TOKEN;

			var tokenUrl = AuthSettings.AUTH_URL_REFRESH_TOKEN;
			
			var tokenHttp = new HTTPConnection();
			var tokenConnectionId = BackyardSettings.AUTH_CONNECTIONID_REFRESH_TOKEN_PREFIX + Guid.NewGuid().ToString();
			
			Debug.LogWarning("内部に保存していたrefresh tokenを使って、リクエストを作り出す。");
			Debug.LogError("refresh tokenを使おう。いまのところまだダミーid使ってる。");

			var tokenRequestHeaders = new Dictionary<string, string>{
				{"identity", "dummy-id"}
			};

			Observable.FromCoroutine(
				() => tokenHttp.Get(
					tokenConnectionId,
					tokenRequestHeaders,
					tokenUrl,
					(conId, code, responseHeaders, data) => {
						EvaluateTokenResult(conId, responseHeaders, code, data);
					},
					(conId, code, failedReason, responseHeaders) => {
						EvaluateTokenResult(conId, responseHeaders, code, failedReason);
					}
				)
			).Timeout(
				TimeSpan.FromSeconds(BackyardSettings.HTTP_TIMEOUT_SEC)
			).Subscribe(
				_ => {},
				ex => {
					var errorType = ex.GetType();

					switch (errorType.ToString()) {
						case BackyardSettings.AUTH_HTTP_INTERNALERROR_TYPE_TIMEOUT: {
							EvaluateTokenResult(tokenConnectionId, new Dictionary<string, string>(), BackyardSettings.AUTH_HTTP_INTERNALERROR_CODE_TIMEOUT, "timeout:" + ex.ToString());
							break;
						}
						default: {
							throw new Exception("failed to get token by undefined reason:" + ex.Message);
						}
					}
				}
			);
		}

		private void EvaluateTokenResult (string tokenConnectionId, Dictionary<string, string> responseHeaders, int responseCode, string resultDataOrFailedReason) {
			ErrorFlowHandling(
				tokenConnectionId,
				responseHeaders, 
				responseCode,  
				resultDataOrFailedReason, 
				(succeededConId, succeededData) => {
					var isValid = IsTokenExist(succeededData);
					
					if (isValid) {
						// Debug.LogWarning("token取得に成功 succeededData:" + succeededData);
						var tokenCandidate = succeededData;
						UpdateTokenThenAttemptLogin(tokenCandidate);
					} else {
						Debug.LogError("未解決の、invalidなtokenだと見做せた場合の処理");
					}
				},
				(failedConId, failedCode, failedReason) => {
					_loginState = LoginState.LOGGED_OUT;
					
					if (IsInMaintenance(responseCode, responseHeaders)) {
						// in maintenance, do nothing here.
						return;
					}

					if (IsAuthFailed(responseCode, responseHeaders)) {
						// get token url should not return unauthorized response. do nothing here.
						return;
					}

					Debug.LogError("failedConId:" + failedConId + " failedReason:" + failedReason);

					// other errors. 
					var shouldRetry = OnAuthFailed(tokenConnectionId, resultDataOrFailedReason);
					if (shouldRetry) {
						Debug.LogError("なんかtoken取得からリトライすべきなんだけどちょっとまってな1");
						// GetTokenThenLogin();
					} 
				}
			);
		}

		private bool IsTokenExist (string tokenCandidate) {
			if (string.IsNullOrEmpty(tokenCandidate)) return false;
			return true;
		}


		/**
			step 2 of 2 :token refreshing.
				login with token candidate.
				this method constructs kind of "Autoya's popular auth-signed http request".
		*/
		private void AttemptLoginByTokenCandidate (string tokenCandidate) {
			_loginState = LoginState.LOGGING_IN;

			/*
				set token candidate and identitiy to request header basement.
			*/
			SetHTTPAuthorizedPart("dummy-id", tokenCandidate);

			/*
				create login request.
			*/
			var loginUrl = AuthSettings.AUTH_URL_LOGIN;			
			var loginHeaders = GetAuthorizedAndAdditionalHeaders();

			var loginHttp = new HTTPConnection();
			var loginConnectionId = BackyardSettings.AUTH_CONNECTIONID_ATTEMPTLOGIN_PREFIX + Guid.NewGuid().ToString();
			
			Observable.FromCoroutine(
				_ => loginHttp.Get(
					loginConnectionId,
					loginHeaders,
					loginUrl,
					(conId, code, responseHeaders, data) => {
						EvaluateLoginResult(conId, responseHeaders, code, data);
					},
					(conId, code, failedReason, responseHeaders) => {
						EvaluateLoginResult(conId, responseHeaders, code, failedReason);
					}
				)
			).Timeout(
				TimeSpan.FromSeconds(BackyardSettings.HTTP_TIMEOUT_SEC)
			).Subscribe(
				_ => {},
				ex => {
					var errorType = ex.GetType();

					switch (errorType.ToString()) {
						case BackyardSettings.AUTH_HTTP_INTERNALERROR_TYPE_TIMEOUT: {
							EvaluateLoginResult(loginConnectionId, new Dictionary<string, string>(), BackyardSettings.AUTH_HTTP_INTERNALERROR_CODE_TIMEOUT, "timeout:" + ex.ToString());
							break;
						}
						default: {
							throw new Exception("failed to get token by undefined reason:" + ex.Message);
						}
					}
				}
			);
		}

		private void EvaluateLoginResult (string loginConnectionId, Dictionary<string, string> responseHeaders, int responseCode, string resultDataOrFailedReason) {
			ErrorFlowHandling(
				loginConnectionId,
				responseHeaders, 
				responseCode,  
				resultDataOrFailedReason, 
				(succeededConId, succeededData) => {
					var savedToken = LoadToken();
					Debug.Log("should validate token.");
					OnLoginSucceeded(savedToken);
				},
				(failedConId, failedCode, failedReason) => {
					// if Unauthorized, OnAuthFailed is already called.
					if (IsAuthFailed(responseCode, responseHeaders)) return;
			
					_loginState = LoginState.LOGGED_OUT;

					/*
						we should handling NOT 401(Unauthorized) result.
					*/

					Debug.LogError("failedConId:" + failedConId + " failedReason:" + failedReason);
					
					// tokenはあったんだけど通信失敗とかで予定が狂ったケースか。
					// tokenはあるんで、エラーわけを細かくやって、なんともできなかったら再チャレンジっていうコースな気がする。
					
					var shouldRetry = OnAuthFailed(loginConnectionId, resultDataOrFailedReason);
					if (shouldRetry) {
						Debug.LogError("トークン取得、すぐに再開すべきかどうかは疑問。ちょっと時間おくとかがしたい。一定時間後に実行、というのがいいと思う。現状ではまだリトライしていない。");
						// Login();
					}
				}
			);
		}
		
		private void UpdateTokenThenAttemptLogin (string gotNewToken) {
			var isSaved = SaveToken(gotNewToken);
			if (isSaved) {
				Login();
			} else {
				Debug.LogError("tokenのSaveに失敗、この辺、保存周りのイベントと連携させないとな〜〜〜");
			}
		}
		private void RevokeToken () {
			_token = string.Empty;
			var isRevoked = SaveToken(string.Empty);
			if (!isRevoked) Debug.LogError("revokeに失敗、この辺、保存周りのイベントと連携させないとな〜〜〜");
		}


		/*
			public auth APIs
		*/

		/**
			returns auth progress int (0 ~ 4)
		*/
		public static int Auth_Progress () {
			return autoya.Progress();
		}

		/**
			return true if started to attempt log-in.

			if this method returns false, attempt of Login is already ongoing.
		*/
		public static bool Auth_AttemptLogIn () {
			if (autoya._loginState ==LoginState.LOGGED_OUT) {
				autoya.Login();
				return true;
			}
			return false;
		}


		public static void Auth_SetOnLoginSucceeded (Action onAuthSucceeded) {
			autoya.OnLoginSucceeded = token => {
				autoya.LoggedIn(token);
				onAuthSucceeded();
			};
			
			// if already logged in, fire immediately.
			if (Auth_IsLoggedIn()) onAuthSucceeded();
        }


		/**
			Note that:this method is not pair of above "Auth_SetOnLoginSucceeded" method.

		*/
		public static void Auth_SetOnAuthFailed (Func<string, string, bool> onAuthFailed) {
            autoya.OnAuthFailed = (conId, reason) => {
				autoya.LogOut();
				return onAuthFailed(conId, reason);
			};
        }

		public static void Auth_Logout () {
			autoya.LogOut();
		}
		
		private Action<string> OnLoginSucceeded;

		/**
			this method will be called when Autoya encountered "auth failed".
			caused by: received 401 response by some reason will raise this method.

			1.server returns 401 as the result of login request.
			2.server returns 401 as the result of usual http connection.

			and this method DOES NOT FIRE when logout intentionally.
		*/
		private Func<string, string, bool> OnAuthFailed;


		
		public enum LoginState : int {
			LOGGED_OUT,
			GETTING_TOKEN,
			LOGGING_IN,
			LOGGED_IN,
			REFRESHING_TOKEN,
		}

		private LoginState _loginState;

		public static bool Auth_IsLoggedIn () {
			if (string.IsNullOrEmpty(autoya._token)) return false;
			if (autoya._loginState != LoginState.LOGGED_IN) return false; 
			return true;
		}
		
		/*
			test methods.
		*/
		public static void Auth_Test_CreateAuthError () {
			/*
				generate fake response for generate fake accidential logout error.
			*/
			autoya.ErrorFlowHandling(
				"Auth_Test_AccidentialLogout_ConnectionId", 
				new Dictionary<string, string>(),
				401, 
				"Auth_Test_AccidentialLogout test error", 
				(conId, data) => {}, 
				(conId, code, reason) => {}
			);
		}
		
	}
}