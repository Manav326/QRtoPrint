#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::io::{BufRead, BufReader, Write};
use std::time::Duration;

const AGENT_STATUS_URL: &str = "http://127.0.0.1:17842/api/v1/local/status";
const AGENT_PIPE_NAME: &str = "QRtoPrint.SmartPrint.Agent.Control";

fn agent_pipe_path() -> String {
    format!(r"\\.\pipe\{AGENT_PIPE_NAME}")
}
const SESSION_TARGET: &str = "QRtoPrint.SmartPrint.Session";

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LocalAgentStatus {
    service_reachable: bool,
    local_api_reachable: bool,
    paired: bool,
    agent_device_id: Option<String>,
    status: String,
    api_base_url: Option<String>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentPipeResponse {
    success: bool,
    agent_device_id: Option<String>,
    message: Option<String>,
    service_reachable: Option<bool>,
    paired: Option<bool>,
    status: Option<String>,
    local_api_reachable: Option<bool>,
    api_base_url: Option<String>,
    code: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentPipeRequest<'a> {
    operation: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    enrollment_code: Option<&'a str>,
}

#[tauri::command]
async fn get_local_agent_status() -> LocalAgentStatus {
    let client = match reqwest::Client::builder().timeout(Duration::from_secs(2)).build() {
        Ok(client) => client,
        Err(_) => return pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
    };

    match client.get(AGENT_STATUS_URL).send().await {
        Ok(response) if response.status().as_u16() == 503 => {
            let problem = response.json::<serde_json::Value>().await.ok();
            let title = problem.as_ref()
                .and_then(|value| value.get("title"))
                .and_then(serde_json::Value::as_str);
            let api_base_url = problem.as_ref()
                .and_then(|value| value.get("apiBaseUrl"))
                .and_then(serde_json::Value::as_str)
                .map(str::to_owned);
            if title == Some("This computer's SmartPrint pairing was revoked by its shop owner.") {
                LocalAgentStatus {
                    service_reachable: true,
                    local_api_reachable: true,
                    paired: false,
                    agent_device_id: None,
                    status: "REVOKED".into(),
                    api_base_url,
                }
            } else if title == Some("This computer is not paired with a print shop.") {
                LocalAgentStatus {
                    service_reachable: true,
                    local_api_reachable: true,
                    paired: false,
                    agent_device_id: None,
                    status: "UNPAIRED".into(),
                    api_base_url,
                }
            } else {
                pipe_agent_status().unwrap_or_else(|_| unavailable_status())
            }
        },
        Ok(response) if response.status().is_success() => {
            match response.json::<serde_json::Value>().await {
                Ok(payload) => {
                    let id = payload.get("agentDeviceId")
                        .and_then(serde_json::Value::as_str)
                        .map(str::to_owned);
                    LocalAgentStatus {
                        service_reachable: true,
                        local_api_reachable: true,
                        paired: id.is_some(),
                        agent_device_id: id,
                        status: payload.get("status")
                            .and_then(serde_json::Value::as_str)
                            .unwrap_or("CONNECTED")
                            .to_owned(),
                        api_base_url: payload.get("apiBaseUrl")
                            .and_then(serde_json::Value::as_str)
                            .map(str::to_owned),
                    }
                }
                Err(_) => pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
            }
        }
        Ok(_) | Err(_) => pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
    }
}

fn unavailable_status() -> LocalAgentStatus {
    LocalAgentStatus {
        service_reachable: false,
        local_api_reachable: false,
        paired: false,
        agent_device_id: None,
        status: "SERVICE_UNAVAILABLE".into(),
        api_base_url: None,
    }
}

#[cfg(windows)]
fn send_agent_pipe_request(request: &AgentPipeRequest<'_>) -> Result<AgentPipeResponse, String> {
    use std::fs::OpenOptions;

    let mut pipe = OpenOptions::new()
        .read(true)
        .write(true)
        .open(agent_pipe_path())
        .map_err(|_| "The SmartPrint background service is not ready. Please retry shortly.".to_string())?;

    let request = serde_json::to_vec(request)
        .map_err(|_| "Could not prepare the SmartPrint service request.".to_string())?;
    pipe.write_all(&request)
        .and_then(|_| pipe.write_all(b"\n"))
        .and_then(|_| pipe.flush())
        .map_err(|_| "Could not send the request to the SmartPrint background service.".to_string())?;

    let mut response_line = String::new();
    BufReader::new(pipe)
        .read_line(&mut response_line)
        .map_err(|_| "The SmartPrint background service did not return a response.".to_string())?;
    serde_json::from_str(&response_line)
        .map_err(|_| "The SmartPrint background service returned an invalid response.".to_string())
}

#[cfg(windows)]
fn pipe_agent_status() -> Result<LocalAgentStatus, String> {
    let response = send_agent_pipe_request(&AgentPipeRequest {
        operation: "status",
        enrollment_code: None,
    })?;
    if response.service_reachable != Some(true) {
        return Err(response.message.unwrap_or_else(|| "The SmartPrint background service is not responding.".into()));
    }

    let paired = response.paired.unwrap_or(false);
    Ok(LocalAgentStatus {
        service_reachable: true,
        local_api_reachable: response.local_api_reachable.unwrap_or(false),
        paired,
        agent_device_id: response.agent_device_id,
        status: response.status.unwrap_or_else(|| if paired { "CONNECTED" } else { "UNPAIRED" }.into()),
        api_base_url: response.api_base_url,
    })
}

#[cfg(not(windows))]
fn pipe_agent_status() -> Result<LocalAgentStatus, String> {
    Err("The local Windows Agent pipe is unavailable on this platform.".into())
}

#[tauri::command]
async fn enroll_local_agent(enrollment_code: String) -> Result<AgentPipeResponse, String> {
    if enrollment_code.len() != 32 ||
        !enrollment_code.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_') {
        return Err("The service returned an invalid one-time enrollment authorization.".into());
    }

    #[cfg(windows)]
    {
        let response = send_agent_pipe_request(&AgentPipeRequest {
            operation: "enroll",
            enrollment_code: Some(&enrollment_code),
        })?;
        if !response.success && response.code.is_none() {
            return Err(response.message.clone().unwrap_or_else(|| "This computer could not be connected to the shop.".into()));
        }
        Ok(response)
    }

    #[cfg(not(windows))]
    {
        let _ = enrollment_code;
        Err("SmartPrint desktop enrollment is supported on Windows only.".into())
    }
}

#[tauri::command]
fn read_secure_session() -> Result<Option<String>, String> {
    #[cfg(windows)]
    { credential_store::read() }
    #[cfg(not(windows))]
    { Ok(None) }
}

#[tauri::command]
fn write_secure_session(session: Option<String>) -> Result<(), String> {
    #[cfg(windows)]
    { credential_store::write(session) }
    #[cfg(not(windows))]
    { let _ = session; Ok(()) }
}

#[cfg(windows)]
mod credential_store {
    use super::SESSION_TARGET;
    use std::ptr::null_mut;
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW,
        CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC,
    };

    fn target() -> Vec<u16> {
        SESSION_TARGET.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn read() -> Result<Option<String>, String> {
        unsafe {
            let target = target();
            let mut credential: *mut CREDENTIALW = null_mut();
            if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) == 0 {
                let error = GetLastError();
                if error == ERROR_NOT_FOUND { return Ok(None); }
                return Err(format!("Windows secure storage could not be read ({error})."));
            }
            let item = &*credential;
            let bytes = if item.CredentialBlobSize == 0 {
                Vec::new()
            } else {
                std::slice::from_raw_parts(item.CredentialBlob, item.CredentialBlobSize as usize).to_vec()
            };
            CredFree(credential.cast());
            String::from_utf8(bytes)
                .map(Some)
                .map_err(|_| "The saved desktop session is invalid. Please sign in again.".into())
        }
    }

    pub fn write(session: Option<String>) -> Result<(), String> {
        unsafe {
            let mut target_name = target();
            match session {
                None => {
                    if CredDeleteW(target_name.as_ptr(), CRED_TYPE_GENERIC, 0) == 0 {
                        let error = GetLastError();
                        if error != ERROR_NOT_FOUND {
                            return Err(format!("Windows secure storage could not clear the session ({error})."));
                        }
                    }
                }
                Some(value) => {
                    if value.len() > 2_400 {
                        return Err("The saved session exceeds the secure storage limit.".into());
                    }
                    let mut bytes = value.into_bytes();
                    let mut user_name: Vec<u16> = "SmartPrint".encode_utf16().chain(std::iter::once(0)).collect();
                    let mut credential: CREDENTIALW = std::mem::zeroed();
                    credential.Type = CRED_TYPE_GENERIC;
                    credential.TargetName = target_name.as_mut_ptr();
                    credential.CredentialBlobSize = bytes.len() as u32;
                    credential.CredentialBlob = bytes.as_mut_ptr();
                    credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
                    credential.UserName = user_name.as_mut_ptr();
                    if CredWriteW(&credential, 0) == 0 {
                        let error = GetLastError();
                        bytes.fill(0);
                        return Err(format!("Windows secure storage could not save the session ({error})."));
                    }
                    bytes.fill(0);
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::agent_pipe_path;

    #[test]
    fn agent_pipe_uses_the_canonical_windows_named_pipe_namespace() {
        assert_eq!(agent_pipe_path(), r"\\.\pipe\QRtoPrint.SmartPrint.Agent.Control");
    }
}

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            get_local_agent_status,
            enroll_local_agent,
            read_secure_session,
            write_secure_session
        ])
        .run(tauri::generate_context!())
        .expect("error while running SmartPrint desktop");
}
