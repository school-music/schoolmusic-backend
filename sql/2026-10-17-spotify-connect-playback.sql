-- Spotify Connect account credentials; apply once after the base schoolmusic schema.
CREATE TABLE spotify_playback_connection (
    id INT NOT NULL PRIMARY KEY,
    spotify_user_id VARCHAR(128) NOT NULL,
    refresh_token_ciphertext TEXT NOT NULL,
    refresh_token_expires_at DATETIME NOT NULL,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    CONSTRAINT chk_spotify_playback_singleton CHECK (id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
