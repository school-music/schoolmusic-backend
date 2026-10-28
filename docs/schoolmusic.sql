-- phpMyAdmin SQL Dump
-- version 5.2.1
-- https://www.phpmyadmin.net/
--
-- Host: 127.0.0.1
-- Generation Time: Paź 14, 2026 at 06:28 PM
-- Wersja serwera: 10.4.32-MariaDB
-- Wersja PHP: 8.2.12

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
START TRANSACTION;
SET time_zone = "+00:00";


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;

--
-- Database: `schoolmusic`
--

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `artists`
--

CREATE TABLE `artists` (
  `id` int(11) NOT NULL,
  `spotify_artist_id` varchar(255) DEFAULT NULL,
  `name` varchar(500) NOT NULL,
  `source` enum('spotify','youtube','local','other') NOT NULL DEFAULT 'spotify'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

--
-- Dumping data for table `artists`
--

INSERT INTO `artists` (`id`, `spotify_artist_id`, `name`, `source`) VALUES
(1, NULL, 'Ludwig van Beethoven', 'other'),
(2, NULL, 'Wolfgang Amadeus Mozart', 'other'),
(3, NULL, 'Johann Sebastian Bach', 'other'),
(4, NULL, 'Frederic Chopin', 'other'),
(5, NULL, 'Antonio Vivaldi', 'other'),
(6, NULL, 'Pyotr Ilyich Tchaikovsky', 'other'),
(7, NULL, 'Claude Debussy', 'other'),
(8, NULL, 'Johannes Brahms', 'other'),
(9, NULL, 'Erik Satie', 'other'),
(10, NULL, 'Scott Joplin', 'other'),
(11, NULL, 'Duke Ellington', 'other'),
(12, NULL, 'Louis Armstrong', 'other'),
(13, NULL, 'George Gershwin', 'other'),
(14, NULL, 'Chill Study Beats', 'other'),
(15, NULL, 'Coffee Shop Lo-Fi', 'other'),
(16, NULL, 'Synthwave Collective', 'other'),
(17, NULL, 'Ambient Soundscapes', 'other'),
(18, NULL, 'Cyber Dreams', 'other'),
(19, NULL, 'Acoustic Sunsets', 'other'),
(20, NULL, 'Jazz Cafe Quartet', 'other');

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `artist_blacklist`
--

CREATE TABLE `artist_blacklist` (
  `id` int(11) NOT NULL,
  `artist_id` int(11) NOT NULL,
  `moderator_id` int(11) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `banned_users`
--

CREATE TABLE `banned_users` (
  `id` int(11) NOT NULL,
  `user_id` int(11) NOT NULL,
  `reason` text DEFAULT NULL,
  `moderator_id` int(11) NOT NULL,
  `banned_at` datetime NOT NULL DEFAULT current_timestamp()
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `break`
--

CREATE TABLE `break` (
  `id` int(11) NOT NULL,
  `break_date` date NOT NULL,
  `break_number` tinyint(4) NOT NULL,
  `start_at` datetime NOT NULL,
  `ends_at` datetime NOT NULL,
  `vote_ends` datetime DEFAULT NULL,
  `music_enabled` tinyint(1) NOT NULL DEFAULT 1
) ;

--
-- Dumping data for table `break`
--

INSERT INTO `break` (`id`, `break_date`, `break_number`, `start_at`, `ends_at`, `vote_ends`, `music_enabled`) VALUES
(1, '2026-01-01', 1, '2026-01-01 07:45:00', '2026-01-01 07:55:00', '2026-01-01 07:45:00', 1),
(2, '2026-01-01', 2, '2026-01-01 08:40:00', '2026-01-01 08:50:00', '2026-01-01 08:40:00', 1),
(3, '2026-01-01', 3, '2026-01-01 09:35:00', '2026-01-01 09:45:00', '2026-01-01 09:35:00', 1),
(4, '2026-01-01', 4, '2026-01-01 10:30:00', '2026-01-01 10:50:00', '2026-01-01 10:30:00', 1),
(5, '2026-01-01', 5, '2026-01-01 11:35:00', '2026-01-01 11:45:00', '2026-01-01 11:35:00', 1),
(6, '2026-01-01', 6, '2026-01-01 12:30:00', '2026-01-01 12:40:00', '2026-01-01 12:30:00', 1),
(7, '2026-01-01', 7, '2026-01-01 13:25:00', '2026-01-01 13:35:00', '2026-01-01 13:25:00', 1),
(8, '2026-01-01', 8, '2026-01-01 14:20:00', '2026-01-01 14:25:00', '2026-01-01 14:20:00', 1);

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `break_templates`
--

CREATE TABLE `break_templates` (
  `id` int(11) NOT NULL,
  `break_number` tinyint(4) NOT NULL,
  `starts_time` time NOT NULL,
  `ends_time` time NOT NULL
) ;

--
-- Dumping data for table `break_templates`
--

INSERT INTO `break_templates` (`id`, `break_number`, `starts_time`, `ends_time`) VALUES
(1, 1, '07:45:00', '07:55:00'),
(2, 2, '08:40:00', '08:50:00'),
(3, 3, '09:35:00', '09:45:00'),
(4, 4, '10:30:00', '10:50:00'),
(5, 5, '11:35:00', '11:45:00'),
(6, 6, '12:30:00', '12:40:00'),
(7, 7, '13:25:00', '13:35:00'),
(8, 8, '14:20:00', '14:25:00');

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `default_songs`
--

CREATE TABLE `default_songs` (
  `id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `exception_break`
--

CREATE TABLE `exception_break` (
  `id` int(11) NOT NULL,
  `break_date` date NOT NULL,
  `break_number` tinyint(4) NOT NULL,
  `music_enabled` tinyint(1) NOT NULL,
  `description` text DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `exception_day`
--

CREATE TABLE `exception_day` (
  `id` int(11) NOT NULL,
  `start_date` date NOT NULL,
  `end_date` date NOT NULL,
  `day_type` enum('day_off','no_music') NOT NULL,
  `description` text DEFAULT NULL
) ;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `history`
--

CREATE TABLE `history` (
  `id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL,
  `break_id` int(11) NOT NULL,
  `played_at` datetime NOT NULL DEFAULT current_timestamp(),
  `votes` int(11) NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `moderators`
--

CREATE TABLE `moderators` (
  `id` int(11) NOT NULL,
  `user_id` int(11) NOT NULL,
  `artists_banned` int(11) NOT NULL DEFAULT 0,
  `songs_banned` int(11) NOT NULL DEFAULT 0,
  `songs_approved` int(11) NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

--
-- Dumping data for table `moderators`
--

INSERT INTO `moderators` (`id`, `user_id`, `artists_banned`, `songs_banned`, `songs_approved`) VALUES
(1, 1, 0, 0, 1);

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `playback_state`
--

CREATE TABLE `playback_state` (
  `id` tinyint(4) NOT NULL DEFAULT 1,
  `current_queue_item_id` int(11) DEFAULT NULL,
  `started_at` datetime DEFAULT NULL,
  `is_paused` tinyint(1) NOT NULL DEFAULT 0,
  `updated_at` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp()
) ;

--
-- Dumping data for table `playback_state`
--

INSERT INTO `playback_state` (`id`, `current_queue_item_id`, `started_at`, `is_paused`, `updated_at`) VALUES
(1, NULL, NULL, 0, '2026-10-14 16:15:31');

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `queue_items`
--

CREATE TABLE `queue_items` (
  `id` int(11) NOT NULL,
  `break_id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL,
  `user_id` int(11) DEFAULT NULL,
  `greenlist_id` int(11) DEFAULT NULL,
  `blacklist_id` int(11) DEFAULT NULL,
  `moderator_id` int(11) DEFAULT NULL,
  `moderation_status` enum('approved','rejected') DEFAULT NULL,
  `status` enum('queued','playing','played','skipped') NOT NULL DEFAULT 'queued',
  `order_index` int(11) NOT NULL,
  `created_at` datetime NOT NULL DEFAULT current_timestamp()
) ;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `ranks`
--

CREATE TABLE `ranks` (
  `id` int(11) NOT NULL,
  `rank` varchar(255) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

--
-- Dumping data for table `ranks`
--

INSERT INTO `ranks` (`id`, `rank`) VALUES
(3, 'admin'),
(4, 'head_admin'),
(2, 'mod'),
(1, 'user');

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `songs`
--

CREATE TABLE `songs` (
  `id` int(11) NOT NULL,
  `spotify_id` varchar(255) DEFAULT NULL,
  `source` enum('spotify','youtube','local','other') NOT NULL DEFAULT 'spotify',
  `title` varchar(500) NOT NULL,
  `artist_id` int(11) NOT NULL,
  `cover` text DEFAULT NULL,
  `song_url` text DEFAULT NULL,
  `genre` varchar(100) DEFAULT NULL,
  `duration_ms` int(11) NOT NULL,
  `is_active` tinyint(1) NOT NULL DEFAULT 1,
  `created_at` datetime NOT NULL DEFAULT current_timestamp()
) ;

--
-- Dumping data for table `songs`
--

INSERT INTO `songs` (`id`, `spotify_id`, `source`, `title`, `artist_id`, `cover`, `song_url`, `genre`, `duration_ms`, `is_active`, `created_at`) VALUES
(1, NULL, 'other', 'Symphony No. 5 in C Minor - I. Allegro con brio', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-symphony-no.-5/01.%20Allegro%20con%20brio.mp3', 'Classical', 432000, 1, '2026-10-14 12:00:00'),
(2, NULL, 'other', 'Symphony No. 5 in C Minor - II. Andante con moto', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-symphony-no.-5/02.%20Andante%20con%20moto.mp3', 'Classical', 601000, 1, '2026-10-14 12:00:00'),
(3, NULL, 'other', 'Moonlight Sonata - 1st Movement', 1, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/MoonlightSonata_755/Beethoven-MoonlightSonata.mp3', 'Classical', 315000, 1, '2026-10-14 12:00:00'),
(4, NULL, 'other', 'Fur Elise', 1, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/FurElise_787/FurElise.mp3', 'Classical', 178000, 1, '2026-10-14 12:00:00'),
(5, NULL, 'other', 'Symphony No. 9 - Ode to Joy', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-symphony-9-ode-to-joy/OdeToJoy.mp3', 'Classical', 345000, 1, '2026-10-14 12:00:00'),
(6, NULL, 'other', 'Eine kleine Nachtmusik - I. Allegro', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/EineKleineNachtmusik_942/01_Allegro.mp3', 'Classical', 348000, 1, '2026-10-14 12:00:00'),
(7, NULL, 'other', 'Eine kleine Nachtmusik - II. Romance', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/EineKleineNachtmusik_942/02_Romanze.mp3', 'Classical', 355000, 1, '2026-10-14 12:00:00'),
(8, NULL, 'other', 'Symphony No. 40 in G Minor - I. Molto Allegro', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/MozartSymphony40/01_Molto_allegro.mp3', 'Classical', 460000, 1, '2026-10-14 12:00:00'),
(9, NULL, 'other', 'Rondo alla Turca (Turkish March)', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/rondo-alla-turca-mozart/TurkishMarch.mp3', 'Classical', 212000, 1, '2026-10-14 12:00:00'),
(10, NULL, 'other', 'Clarinet Concerto in A Major - Adagio', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/MozartClarinetConcertoAdagio/ClarinetAdagio.mp3', 'Classical', 410000, 1, '2026-10-14 12:00:00'),
(11, NULL, 'other', 'Toccata and Fugue in D Minor', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/ToccataAndFugueInDMinor_990/ToccataAndFugueInDMinor.mp3', 'Classical', 560000, 1, '2026-10-14 12:00:00'),
(12, NULL, 'other', 'Air on the G String', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/BachAirOnTheGString/AirOnGString.mp3', 'Classical', 290000, 1, '2026-10-14 12:00:00'),
(13, NULL, 'other', 'Brandenburg Concerto No. 3 - I. Allegro', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/BachBrandenburgConcerto3/01_Allegro.mp3', 'Classical', 380000, 1, '2026-10-14 12:00:00'),
(14, NULL, 'other', 'Cello Suite No. 1 in G Major - Prelude', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/BachCelloSuiteNo1InGMajorPrelude/CelloSuite1.mp3', 'Classical', 154000, 1, '2026-10-14 12:00:00'),
(15, NULL, 'other', 'Nocturne in E-flat Major, Op. 9 No. 2', 4, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/ChopinNocturneOp9No2/Chopin_Nocturne_Op9_No2.mp3', 'Classical', 270000, 1, '2026-10-14 12:00:00'),
(16, NULL, 'other', 'Minute Waltz in D-flat Major', 4, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/ChopinMinuteWaltz/MinuteWaltz.mp3', 'Classical', 115000, 1, '2026-10-14 12:00:00'),
(17, NULL, 'other', 'Prelude in D-flat Major Raindrop', 4, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/ChopinRaindropPrelude/RaindropPrelude.mp3', 'Classical', 320000, 1, '2026-10-14 12:00:00'),
(18, NULL, 'other', 'Fantaisie-Impromptu, Op. 66', 4, 'https://images.unsplash.com/photo-1520523839898-50712705494a?w=400', 'https://archive.org/download/ChopinFantaisieImpromptuOp66/FantaisieImpromptu.mp3', 'Classical', 310000, 1, '2026-10-14 12:00:00'),
(19, NULL, 'other', 'The Four Seasons - Spring: I. Allegro', 5, 'https://images.unsplash.com/photo-1518709268805-4e9042af9f23?w=400', 'https://archive.org/download/VivaldiFourSeasonsSpring/Spring_Allegro.mp3', 'Classical', 205000, 1, '2026-10-14 12:00:00'),
(20, NULL, 'other', 'The Four Seasons - Summer: III. Presto', 5, 'https://images.unsplash.com/photo-1518709268805-4e9042af9f23?w=400', 'https://archive.org/download/VivaldiFourSeasonsSummer/Summer_Presto.mp3', 'Classical', 165000, 1, '2026-10-14 12:00:00'),
(21, NULL, 'other', 'The Four Seasons - Autumn: I. Allegro', 5, 'https://images.unsplash.com/photo-1518709268805-4e9042af9f23?w=400', 'https://archive.org/download/VivaldiFourSeasonsAutumn/Autumn_Allegro.mp3', 'Classical', 310000, 1, '2026-10-14 12:00:00'),
(22, NULL, 'other', 'The Four Seasons - Winter: I. Allegro non molto', 5, 'https://images.unsplash.com/photo-1518709268805-4e9042af9f23?w=400', 'https://archive.org/download/VivaldiFourSeasonsWinter/Winter_Allegro.mp3', 'Classical', 215000, 1, '2026-10-14 12:00:00'),
(23, NULL, 'other', 'Swan Lake - Scene (Act II)', 6, 'https://images.unsplash.com/photo-1508700115892-45ecd05ae2ad?w=400', 'https://archive.org/download/TchaikovskySwanLakeScene/SwanLakeScene.mp3', 'Classical', 175000, 1, '2026-10-14 12:00:00'),
(24, NULL, 'other', 'The Nutcracker - Dance of the Sugar Plum Fairy', 6, 'https://images.unsplash.com/photo-1508700115892-45ecd05ae2ad?w=400', 'https://archive.org/download/TchaikovskyNutcrackerSugarPlum/SugarPlumFairy.mp3', 'Classical', 112000, 1, '2026-10-14 12:00:00'),
(25, NULL, 'other', 'The Nutcracker - Waltz of the Flowers', 6, 'https://images.unsplash.com/photo-1508700115892-45ecd05ae2ad?w=400', 'https://archive.org/download/TchaikovskyWaltzOfTheFlowers/WaltzOfFlowers.mp3', 'Classical', 415000, 1, '2026-10-14 12:00:00'),
(26, NULL, 'other', 'Clair de Lune', 7, 'https://images.unsplash.com/photo-1519681393784-d120267933ba?w=400', 'https://archive.org/download/DebussyClairDeLune_897/ClairDeLune.mp3', 'Classical', 305000, 1, '2026-10-14 12:00:00'),
(27, NULL, 'other', 'Arabesque No. 1', 7, 'https://images.unsplash.com/photo-1519681393784-d120267933ba?w=400', 'https://archive.org/download/DebussyArabesque1/Arabesque1.mp3', 'Classical', 280000, 1, '2026-10-14 12:00:00'),
(28, NULL, 'other', 'Hungarian Dance No. 5 in G Minor', 8, 'https://images.unsplash.com/photo-1465847899084-d164df4dedc6?w=400', 'https://archive.org/download/BrahmsHungarianDance5/HungarianDance5.mp3', 'Classical', 185000, 1, '2026-10-14 12:00:00'),
(29, NULL, 'other', 'Gymnopedie No. 1', 9, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/SatieGymnopedieNo1/Gymnopedie1.mp3', 'Classical', 195000, 1, '2026-10-14 12:00:00'),
(30, NULL, 'other', 'Gnossienne No. 1', 9, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/SatieGnossienne1/Gnossienne1.mp3', 'Classical', 210000, 1, '2026-10-14 12:00:00'),
(31, NULL, 'other', 'The Entertainer', 10, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/ScottJoplinTheEntertainer/TheEntertainer.mp3', 'Ragtime', 215000, 1, '2026-10-14 12:00:00'),
(32, NULL, 'other', 'Maple Leaf Rag', 10, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/ScottJoplinMapleLeafRag/MapleLeafRag.mp3', 'Ragtime', 190000, 1, '2026-10-14 12:00:00'),
(33, NULL, 'other', 'Pine Apple Rag', 10, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/ScottJoplinPineAppleRag/PineAppleRag.mp3', 'Ragtime', 205000, 1, '2026-10-14 12:00:00'),
(34, NULL, 'other', 'Solace - A Mexican Serenade', 10, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/ScottJoplinSolace/Solace.mp3', 'Ragtime', 240000, 1, '2026-10-14 12:00:00'),
(35, NULL, 'other', 'Take the A Train', 11, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/DukeEllingtonTakeTheATrain/TakeTheATrain.mp3', 'Jazz', 185000, 1, '2026-10-14 12:00:00'),
(36, NULL, 'other', 'Mood Indigo', 11, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/DukeEllingtonMoodIndigo/MoodIndigo.mp3', 'Jazz', 195000, 1, '2026-10-14 12:00:00'),
(37, NULL, 'other', 'It Dont Mean a Thing', 11, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/DukeEllingtonItDontMeanAThing/ItDontMeanAThing.mp3', 'Jazz', 180000, 1, '2026-10-14 12:00:00'),
(38, NULL, 'other', 'Caravan', 11, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/DukeEllingtonCaravan/Caravan.mp3', 'Jazz', 210000, 1, '2026-10-14 12:00:00'),
(39, NULL, 'other', 'What a Wonderful World', 12, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/LouisArmstrongWonderfulWorld/WonderfulWorld.mp3', 'Jazz', 140000, 1, '2026-10-14 12:00:00'),
(40, NULL, 'other', 'When the Saints Go Marching In', 12, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/LouisArmstrongWhenTheSaints/WhenTheSaints.mp3', 'Jazz', 165000, 1, '2026-10-14 12:00:00'),
(41, NULL, 'other', 'West End Blues', 12, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/LouisArmstrongWestEndBlues/WestEndBlues.mp3', 'Jazz', 195000, 1, '2026-10-14 12:00:00'),
(42, NULL, 'other', 'St. Louis Blues', 12, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/LouisArmstrongStLouisBlues/StLouisBlues.mp3', 'Jazz', 180000, 1, '2026-10-14 12:00:00'),
(43, NULL, 'other', 'Rhapsody in Blue', 13, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/GershwinRhapsodyInBlue/RhapsodyInBlue.mp3', 'Jazz', 540000, 1, '2026-10-14 12:00:00'),
(44, NULL, 'other', 'Summertime', 13, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/GershwinSummertime/Summertime.mp3', 'Jazz', 175000, 1, '2026-10-14 12:00:00'),
(45, NULL, 'other', 'An American in Paris', 13, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/GershwinAnAmericanInParis/AnAmericanInParis.mp3', 'Jazz', 480000, 1, '2026-10-14 12:00:00'),
(46, NULL, 'other', 'Autumn Leaves (Smooth Quartet)', 20, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/JazzCafeAutumnLeaves/AutumnLeaves.mp3', 'Jazz', 230000, 1, '2026-10-14 12:00:00'),
(47, NULL, 'other', 'Blue Bossa Nights', 20, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/JazzCafeBlueBossa/BlueBossa.mp3', 'Jazz', 260000, 1, '2026-10-14 12:00:00'),
(48, NULL, 'other', 'Midnight Jazz Session', 20, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/JazzCafeMidnightSession/MidnightSession.mp3', 'Jazz', 245000, 1, '2026-10-14 12:00:00'),
(49, NULL, 'other', 'Coffee Shop Saxophone', 20, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/JazzCafeSaxophoneVibe/CoffeeSax.mp3', 'Jazz', 215000, 1, '2026-10-14 12:00:00'),
(50, NULL, 'other', 'Rainy Day Stride', 20, 'https://images.unsplash.com/photo-1511671782779-c97d3d27a1d4?w=400', 'https://archive.org/download/JazzCafeRainyDayStride/RainyDayStride.mp3', 'Jazz', 195000, 1, '2026-10-14 12:00:00'),
(51, NULL, 'other', 'Morning Coffee Routine', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/01_Morning_Coffee.mp3', 'Lo-Fi', 154000, 1, '2026-10-14 12:00:00'),
(52, NULL, 'other', 'Late Night Library', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/02_Late_Night_Library.mp3', 'Lo-Fi', 168000, 1, '2026-10-14 12:00:00'),
(53, NULL, 'other', 'Tokyo Rain & Neon', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/03_Tokyo_Rain.mp3', 'Lo-Fi', 182000, 1, '2026-10-14 12:00:00'),
(54, NULL, 'other', 'Books and Warm Tea', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/04_Books_Tea.mp3', 'Lo-Fi', 142000, 1, '2026-10-14 12:00:00'),
(55, NULL, 'other', 'Sunday Homework Chill', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/05_Sunday_Homework.mp3', 'Lo-Fi', 175000, 1, '2026-10-14 12:00:00'),
(56, NULL, 'other', 'Vinyl Cracks and Rhodes', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/06_Vinyl_Cracks.mp3', 'Lo-Fi', 190000, 1, '2026-10-14 12:00:00'),
(57, NULL, 'other', 'Cozy Blanket Beats', 14, 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=400', 'https://archive.org/download/lofi-study-chill-beats/07_Cozy_Blanket.mp3', 'Lo-Fi', 160000, 1, '2026-10-14 12:00:00'),
(58, NULL, 'other', 'Bicycle Ride in Autumn', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/01_Bicycle_Ride.mp3', 'Lo-Fi', 170000, 1, '2026-10-14 12:00:00'),
(59, NULL, 'other', 'Window Seat Dreams', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/02_Window_Seat.mp3', 'Lo-Fi', 155000, 1, '2026-10-14 12:00:00'),
(60, NULL, 'other', 'Espresso Aroma', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/03_Espresso_Aroma.mp3', 'Lo-Fi', 165000, 1, '2026-10-14 12:00:00'),
(61, NULL, 'other', 'Pavement Reflections', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/04_Pavement.mp3', 'Lo-Fi', 185000, 1, '2026-10-14 12:00:00'),
(62, NULL, 'other', 'Midnight Coding Flow', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/05_Midnight_Code.mp3', 'Lo-Fi', 195000, 1, '2026-10-14 12:00:00'),
(63, NULL, 'other', 'Cat on the Windowsill', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/06_Cat_Window.mp3', 'Lo-Fi', 145000, 1, '2026-10-14 12:00:00'),
(64, NULL, 'other', 'Streetlights at Dusk', 15, 'https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w=400', 'https://archive.org/download/coffee-shop-lofi-sessions/07_Streetlights.mp3', 'Lo-Fi', 175000, 1, '2026-10-14 12:00:00'),
(65, NULL, 'other', 'Neon Boulevard 1984', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/01_Neon_Boulevard.mp3', 'Synthwave', 235000, 1, '2026-10-14 12:00:00'),
(66, NULL, 'other', 'Cyber Drive at Night', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/02_Cyber_Drive.mp3', 'Synthwave', 248000, 1, '2026-10-14 12:00:00'),
(67, NULL, 'other', 'Analog Sunset Glow', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/03_Analog_Sunset.mp3', 'Synthwave', 215000, 1, '2026-10-14 12:00:00'),
(68, NULL, 'other', 'Laser Highway Chaser', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/04_Laser_Highway.mp3', 'Synthwave', 260000, 1, '2026-10-14 12:00:00'),
(69, NULL, 'other', 'Arcade Heroes Overdrive', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/05_Arcade_Heroes.mp3', 'Synthwave', 220000, 1, '2026-10-14 12:00:00'),
(70, NULL, 'other', 'Miami Heatwave Nights', 16, 'https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=400', 'https://archive.org/download/synthwave-retro-collection/06_Miami_Heatwave.mp3', 'Synthwave', 240000, 1, '2026-10-14 12:00:00'),
(71, NULL, 'other', 'Digital Runner 2099', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/01_Digital_Runner.mp3', 'Electronic', 210000, 1, '2026-10-14 12:00:00'),
(72, NULL, 'other', 'Matrix Pulse', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/02_Matrix_Pulse.mp3', 'Electronic', 225000, 1, '2026-10-14 12:00:00'),
(73, NULL, 'other', 'Neural Network Glitch', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/03_Neural_Network.mp3', 'Electronic', 195000, 1, '2026-10-14 12:00:00'),
(74, NULL, 'other', 'Sub-Bass Odyssey', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/04_SubBass_Odyssey.mp3', 'Electronic', 240000, 1, '2026-10-14 12:00:00'),
(75, NULL, 'other', 'Hologram Heartbeat', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/05_Hologram_Heartbeat.mp3', 'Electronic', 205000, 1, '2026-10-14 12:00:00'),
(76, NULL, 'other', 'Starlight Velocity', 18, 'https://images.unsplash.com/photo-1518770660439-4636190af475?w=400', 'https://archive.org/download/cyber-dreams-electronic-ep/06_Starlight_Velocity.mp3', 'Electronic', 230000, 1, '2026-10-14 12:00:00'),
(77, NULL, 'other', 'Cosmic Dust Meditation', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/01_Cosmic_Dust.mp3', 'Ambient', 320000, 1, '2026-10-14 12:00:00'),
(78, NULL, 'other', 'Deep Ocean Horizons', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/02_Deep_Ocean.mp3', 'Ambient', 360000, 1, '2026-10-14 12:00:00'),
(79, NULL, 'other', 'Mountain Fog Whispers', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/03_Mountain_Fog.mp3', 'Ambient', 305000, 1, '2026-10-14 12:00:00'),
(80, NULL, 'other', 'Ethereal Aurora Borealis', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/04_Aurora.mp3', 'Ambient', 340000, 1, '2026-10-14 12:00:00'),
(81, NULL, 'other', 'Healing Tibetan Bells', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/05_Healing_Bells.mp3', 'Ambient', 295000, 1, '2026-10-14 12:00:00'),
(82, NULL, 'other', 'Infinite Forest Canopy', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/06_Forest_Canopy.mp3', 'Ambient', 350000, 1, '2026-10-14 12:00:00'),
(83, NULL, 'other', 'Soft Rain on Moss', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/07_Soft_Rain.mp3', 'Ambient', 310000, 1, '2026-10-14 12:00:00'),
(84, NULL, 'other', 'Weightless in Orbit', 17, 'https://images.unsplash.com/photo-1534447677768-be436bb09401?w=400', 'https://archive.org/download/ambient-soundscapes-peace/08_Weightless_Orbit.mp3', 'Ambient', 370000, 1, '2026-10-14 12:00:00'),
(85, NULL, 'other', 'Golden Hour Strumming', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/01_Golden_Hour.mp3', 'Acoustic', 185000, 1, '2026-10-14 12:00:00'),
(86, NULL, 'other', 'Campfire Valley Breeze', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/02_Campfire_Valley.mp3', 'Acoustic', 195000, 1, '2026-10-14 12:00:00'),
(87, NULL, 'other', 'Wooden Porch Memories', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/03_Wooden_Porch.mp3', 'Acoustic', 170000, 1, '2026-10-14 12:00:00'),
(88, NULL, 'other', 'Fingerstyle Morning Dew', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/04_Morning_Dew.mp3', 'Acoustic', 210000, 1, '2026-10-14 12:00:00'),
(89, NULL, 'other', 'Rustic Country Road', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/05_Country_Road.mp3', 'Acoustic', 180000, 1, '2026-10-14 12:00:00'),
(90, NULL, 'other', 'Gentle Waves on Shore', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/06_Gentle_Waves.mp3', 'Acoustic', 225000, 1, '2026-10-14 12:00:00'),
(91, NULL, 'other', 'Autumn Leaves Falling', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/07_Autumn_Falling.mp3', 'Acoustic', 160000, 1, '2026-10-14 12:00:00'),
(92, NULL, 'other', 'Spanish Guitar Serenade', 19, 'https://images.unsplash.com/photo-1510915361894-db8b60106cb1?w=400', 'https://archive.org/download/acoustic-sunsets-unplugged/08_Spanish_Serenade.mp3', 'Acoustic', 240000, 1, '2026-10-14 12:00:00'),
(93, NULL, 'other', 'Violin Concerto in D Major - Allegro', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-violin-concerto/01_Allegro.mp3', 'Classical', 480000, 1, '2026-10-14 12:00:00'),
(94, NULL, 'other', 'Piano Concerto No. 5 Emperor - Adagio', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-emperor-concerto/02_Adagio.mp3', 'Classical', 430000, 1, '2026-10-14 12:00:00'),
(95, NULL, 'other', 'Egmont Overture', 1, 'https://images.unsplash.com/photo-1507838153414-b4b713384a76?w=400', 'https://archive.org/download/beethoven-egmont-overture/Egmont.mp3', 'Classical', 510000, 1, '2026-10-14 12:00:00'),
(96, NULL, 'other', 'Symphony No. 25 in G Minor - Allegro', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/mozart-symphony-25/01_Allegro.mp3', 'Classical', 440000, 1, '2026-10-14 12:00:00'),
(97, NULL, 'other', 'Requiem in D Minor - Lacrimosa', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/mozart-requiem-lacrimosa/Lacrimosa.mp3', 'Classical', 195000, 1, '2026-10-14 12:00:00'),
(98, NULL, 'other', 'The Marriage of Figaro - Overture', 2, 'https://images.unsplash.com/photo-1511192336575-5a79af67a629?w=400', 'https://archive.org/download/mozart-figaro-overture/FigaroOverture.mp3', 'Classical', 250000, 1, '2026-10-14 12:00:00'),
(99, NULL, 'other', 'Jesu, Joy of Mans Desiring', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/bach-jesu-joy/JesuJoy.mp3', 'Classical', 205000, 1, '2026-10-14 12:00:00'),
(100, NULL, 'other', 'Goldberg Variations - Aria', 3, 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=400', 'https://archive.org/download/bach-goldberg-variations/Aria.mp3', 'Classical', 235000, 1, '2026-10-14 12:00:00';

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `song_blacklist`
--

CREATE TABLE `song_blacklist` (
  `id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL,
  `artist_id` int(11) NOT NULL,
  `mod_id` int(11) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `song_greenlist`
--

CREATE TABLE `song_greenlist` (
  `id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL,
  `artist_id` int(11) NOT NULL,
  `mod_id` int(11) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Zastąpiona struktura widoku `song_vote_counts`
-- (See below for the actual view)
--
CREATE TABLE `song_vote_counts` (
`song_id` int(11)
,`title` varchar(500)
,`total_votes` bigint(21)
);

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `special_event`
--

CREATE TABLE `special_event` (
  `id` int(11) NOT NULL,
  `title` varchar(255) DEFAULT NULL,
  `file_url` text NOT NULL,
  `starts_at` datetime NOT NULL DEFAULT current_timestamp(),
  `event_status` enum('pending','playing','done') NOT NULL DEFAULT 'pending'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `users`
--

CREATE TABLE `users` (
  `id` int(11) NOT NULL,
  `user_login` varchar(255) NOT NULL,
  `user_password` varchar(255) NOT NULL,
  `rank_id` int(11) NOT NULL DEFAULT 1,
  `created_at` datetime NOT NULL DEFAULT current_timestamp(),
  `password_changed_at` datetime DEFAULT current_timestamp()
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

--
-- Dumping data for table `users`
--

INSERT INTO `users` (`id`, `user_login`, `user_password`, `rank_id`, `created_at`, `password_changed_at`) VALUES
(1, 'Szymon', '$2a$11$zNoEKWex7gSV3IqX55UHVe1HadXBlp29nt5uhNIG8f3d62PUZdHo.', 4, '2026-09-29 18:22:10', '2026-09-29 18:22:10'),
(2, 'Ola', '$2a$11$UHHiyxzvHTn32q5UjUYu6uHIXQD6Ete.v2zVZ7M6qACTSHRpgrw/e', 1, '2026-10-02 20:28:30', '2026-10-02 20:28:30'),
(3, 'Kamil', '$2a$11$usJnhZ8QDO/OtXgiujQlrO.aUMJS.IFu4K2782dfAS5U4u1EG29wy', 1, '2026-10-09 20:39:45', '2026-10-09 20:39:45');

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `user_favourites`
--

CREATE TABLE `user_favourites` (
  `user_id` int(11) NOT NULL,
  `song_id` int(11) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `votes`
--

CREATE TABLE `votes` (
  `id` int(11) NOT NULL,
  `user_id` int(11) NOT NULL,
  `queue_item_id` int(11) NOT NULL,
  `voted_at` datetime NOT NULL DEFAULT current_timestamp()
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `weekly_break_settings`
--

CREATE TABLE `weekly_break_settings` (
  `weekday` tinyint(4) NOT NULL,
  `break_number` tinyint(4) NOT NULL,
  `music_enabled` tinyint(1) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

-- --------------------------------------------------------

--
-- Struktura tabeli dla tabeli `weekly_schedule`
--

CREATE TABLE `weekly_schedule` (
  `weekday` tinyint(4) NOT NULL,
  `music_enabled` tinyint(1) NOT NULL DEFAULT 1
) ;

--
-- Dumping data for table `weekly_schedule`
--

INSERT INTO `weekly_schedule` (`weekday`, `music_enabled`) VALUES
(1, 1),
(2, 1),
(3, 1),
(4, 1),
(5, 1),
(6, 1),
(7, 1);

-- --------------------------------------------------------

--
-- Struktura widoku `song_vote_counts`
--
DROP TABLE IF EXISTS `song_vote_counts`;

CREATE ALGORITHM=UNDEFINED SQL SECURITY INVOKER VIEW `song_vote_counts`  AS SELECT `s`.`id` AS `song_id`, `s`.`title` AS `title`, count(`v`.`id`) AS `total_votes` FROM ((`songs` `s` left join `queue_items` `qi` on(`qi`.`song_id` = `s`.`id`)) left join `votes` `v` on(`v`.`queue_item_id` = `qi`.`id`)) WHERE `s`.`is_active` = 1 GROUP BY `s`.`id`, `s`.`title` ;

--
-- Indeksy dla zrzutów tabel
--

--
-- Indeksy dla tabeli `artists`
--
ALTER TABLE `artists`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_spotify_artist` (`spotify_artist_id`),
  ADD KEY `idx_artists_name` (`name`(191));

--
-- Indeksy dla tabeli `artist_blacklist`
--
ALTER TABLE `artist_blacklist`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_black_artist` (`artist_id`),
  ADD KEY `idx_artist_black_mod` (`moderator_id`);

--
-- Indeksy dla tabeli `banned_users`
--
ALTER TABLE `banned_users`
  ADD PRIMARY KEY (`id`),
  ADD KEY `idx_banned_user` (`user_id`),
  ADD KEY `idx_banned_mod` (`moderator_id`);

--
-- Indeksy dla tabeli `break`
--
ALTER TABLE `break`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_break_day` (`break_date`,`break_number`),
  ADD KEY `idx_break_start` (`start_at`);

--
-- Indeksy dla tabeli `break_templates`
--
ALTER TABLE `break_templates`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_break_number` (`break_number`);

--
-- Indeksy dla tabeli `default_songs`
--
ALTER TABLE `default_songs`
  ADD PRIMARY KEY (`id`),
  ADD KEY `idx_default_song` (`song_id`);

--
-- Indeksy dla tabeli `exception_break`
--
ALTER TABLE `exception_break`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_exc_break` (`break_date`,`break_number`);

--
-- Indeksy dla tabeli `exception_day`
--
ALTER TABLE `exception_day`
  ADD PRIMARY KEY (`id`),
  ADD KEY `idx_exc_day_range` (`start_date`,`end_date`);

--
-- Indeksy dla tabeli `history`
--
ALTER TABLE `history`
  ADD PRIMARY KEY (`id`),
  ADD KEY `idx_history_song` (`song_id`),
  ADD KEY `idx_history_break` (`break_id`),
  ADD KEY `idx_history_played` (`played_at`);

--
-- Indeksy dla tabeli `moderators`
--
ALTER TABLE `moderators`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_mod_user` (`user_id`);

--
-- Indeksy dla tabeli `playback_state`
--
ALTER TABLE `playback_state`
  ADD PRIMARY KEY (`id`),
  ADD KEY `fk_playback_queue` (`current_queue_item_id`);

--
-- Indeksy dla tabeli `queue_items`
--
ALTER TABLE `queue_items`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_break_song` (`break_id`,`song_id`),
  ADD UNIQUE KEY `uniq_break_order` (`break_id`,`order_index`),
  ADD KEY `idx_queue_song` (`song_id`),
  ADD KEY `idx_queue_user` (`user_id`),
  ADD KEY `idx_queue_green` (`greenlist_id`),
  ADD KEY `idx_queue_black` (`blacklist_id`),
  ADD KEY `idx_queue_mod` (`moderator_id`);

--
-- Indeksy dla tabeli `ranks`
--
ALTER TABLE `ranks`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_rank_name` (`rank`);

--
-- Indeksy dla tabeli `songs`
--
ALTER TABLE `songs`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_spotify_song` (`spotify_id`),
  ADD KEY `idx_songs_artist` (`artist_id`),
  ADD KEY `idx_songs_active` (`is_active`);

--
-- Indeksy dla tabeli `song_blacklist`
--
ALTER TABLE `song_blacklist`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_black_song` (`song_id`),
  ADD KEY `idx_black_artist` (`artist_id`),
  ADD KEY `idx_black_mod` (`mod_id`);

--
-- Indeksy dla tabeli `song_greenlist`
--
ALTER TABLE `song_greenlist`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_green_song` (`song_id`),
  ADD KEY `idx_green_artist` (`artist_id`),
  ADD KEY `idx_green_mod` (`mod_id`);

--
-- Indeksy dla tabeli `special_event`
--
ALTER TABLE `special_event`
  ADD PRIMARY KEY (`id`);

--
-- Indeksy dla tabeli `users`
--
ALTER TABLE `users`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_user_login` (`user_login`),
  ADD KEY `idx_users_rank` (`rank_id`);

--
-- Indeksy dla tabeli `user_favourites`
--
ALTER TABLE `user_favourites`
  ADD PRIMARY KEY (`user_id`,`song_id`),
  ADD KEY `idx_fav_song` (`song_id`);

--
-- Indeksy dla tabeli `votes`
--
ALTER TABLE `votes`
  ADD PRIMARY KEY (`id`),
  ADD UNIQUE KEY `uniq_user_vote` (`user_id`,`queue_item_id`),
  ADD KEY `idx_votes_queue` (`queue_item_id`);

--
-- Indeksy dla tabeli `weekly_break_settings`
--
ALTER TABLE `weekly_break_settings`
  ADD PRIMARY KEY (`weekday`,`break_number`),
  ADD KEY `idx_wbs_break` (`break_number`);

--
-- Indeksy dla tabeli `weekly_schedule`
--
ALTER TABLE `weekly_schedule`
  ADD PRIMARY KEY (`weekday`);

--
-- AUTO_INCREMENT for dumped tables
--

--
-- AUTO_INCREMENT for table `artists`
--
ALTER TABLE `artists`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=21;

--
-- AUTO_INCREMENT for table `artist_blacklist`
--
ALTER TABLE `artist_blacklist`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `banned_users`
--
ALTER TABLE `banned_users`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `break`
--
ALTER TABLE `break`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `break_templates`
--
ALTER TABLE `break_templates`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `default_songs`
--
ALTER TABLE `default_songs`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `exception_break`
--
ALTER TABLE `exception_break`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `exception_day`
--
ALTER TABLE `exception_day`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `history`
--
ALTER TABLE `history`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `moderators`
--
ALTER TABLE `moderators`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=2;

--
-- AUTO_INCREMENT for table `queue_items`
--
ALTER TABLE `queue_items`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `ranks`
--
ALTER TABLE `ranks`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=5;

--
-- AUTO_INCREMENT for table `songs`
--
ALTER TABLE `songs`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=101;

--
-- AUTO_INCREMENT for table `song_blacklist`
--
ALTER TABLE `song_blacklist`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `song_greenlist`
--
ALTER TABLE `song_greenlist`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `special_event`
--
ALTER TABLE `special_event`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- AUTO_INCREMENT for table `users`
--
ALTER TABLE `users`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=4;

--
-- AUTO_INCREMENT for table `votes`
--
ALTER TABLE `votes`
  MODIFY `id` int(11) NOT NULL AUTO_INCREMENT;

--
-- Constraints for dumped tables
--

--
-- Constraints for table `artist_blacklist`
--
ALTER TABLE `artist_blacklist`
  ADD CONSTRAINT `fk_artist_black_artist` FOREIGN KEY (`artist_id`) REFERENCES `artists` (`id`),
  ADD CONSTRAINT `fk_artist_black_mod` FOREIGN KEY (`moderator_id`) REFERENCES `moderators` (`id`);

--
-- Constraints for table `banned_users`
--
ALTER TABLE `banned_users`
  ADD CONSTRAINT `fk_banned_mod` FOREIGN KEY (`moderator_id`) REFERENCES `moderators` (`id`),
  ADD CONSTRAINT `fk_banned_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`);

--
-- Constraints for table `default_songs`
--
ALTER TABLE `default_songs`
  ADD CONSTRAINT `fk_default_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`);

--
-- Constraints for table `history`
--
ALTER TABLE `history`
  ADD CONSTRAINT `fk_history_break` FOREIGN KEY (`break_id`) REFERENCES `break` (`id`),
  ADD CONSTRAINT `fk_history_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`);

--
-- Constraints for table `moderators`
--
ALTER TABLE `moderators`
  ADD CONSTRAINT `fk_moderators_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`);

--
-- Constraints for table `playback_state`
--
ALTER TABLE `playback_state`
  ADD CONSTRAINT `fk_playback_queue` FOREIGN KEY (`current_queue_item_id`) REFERENCES `queue_items` (`id`) ON DELETE SET NULL;

--
-- Constraints for table `queue_items`
--
ALTER TABLE `queue_items`
  ADD CONSTRAINT `fk_queue_black` FOREIGN KEY (`blacklist_id`) REFERENCES `song_blacklist` (`id`),
  ADD CONSTRAINT `fk_queue_break` FOREIGN KEY (`break_id`) REFERENCES `break` (`id`),
  ADD CONSTRAINT `fk_queue_green` FOREIGN KEY (`greenlist_id`) REFERENCES `song_greenlist` (`id`),
  ADD CONSTRAINT `fk_queue_mod` FOREIGN KEY (`moderator_id`) REFERENCES `moderators` (`id`),
  ADD CONSTRAINT `fk_queue_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`),
  ADD CONSTRAINT `fk_queue_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`);

--
-- Constraints for table `songs`
--
ALTER TABLE `songs`
  ADD CONSTRAINT `fk_songs_artist` FOREIGN KEY (`artist_id`) REFERENCES `artists` (`id`);

--
-- Constraints for table `song_blacklist`
--
ALTER TABLE `song_blacklist`
  ADD CONSTRAINT `fk_black_artist` FOREIGN KEY (`artist_id`) REFERENCES `artists` (`id`),
  ADD CONSTRAINT `fk_black_mod` FOREIGN KEY (`mod_id`) REFERENCES `moderators` (`id`),
  ADD CONSTRAINT `fk_black_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`);

--
-- Constraints for table `song_greenlist`
--
ALTER TABLE `song_greenlist`
  ADD CONSTRAINT `fk_green_artist` FOREIGN KEY (`artist_id`) REFERENCES `artists` (`id`),
  ADD CONSTRAINT `fk_green_mod` FOREIGN KEY (`mod_id`) REFERENCES `moderators` (`id`),
  ADD CONSTRAINT `fk_green_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`);

--
-- Constraints for table `users`
--
ALTER TABLE `users`
  ADD CONSTRAINT `fk_users_rank` FOREIGN KEY (`rank_id`) REFERENCES `ranks` (`id`);

--
-- Constraints for table `user_favourites`
--
ALTER TABLE `user_favourites`
  ADD CONSTRAINT `fk_fav_song` FOREIGN KEY (`song_id`) REFERENCES `songs` (`id`),
  ADD CONSTRAINT `fk_fav_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`);

--
-- Constraints for table `votes`
--
ALTER TABLE `votes`
  ADD CONSTRAINT `fk_votes_queue` FOREIGN KEY (`queue_item_id`) REFERENCES `queue_items` (`id`),
  ADD CONSTRAINT `fk_votes_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`);

--
-- Constraints for table `weekly_break_settings`
--
ALTER TABLE `weekly_break_settings`
  ADD CONSTRAINT `fk_wbs_break` FOREIGN KEY (`break_number`) REFERENCES `break_templates` (`break_number`),
  ADD CONSTRAINT `fk_wbs_weekday` FOREIGN KEY (`weekday`) REFERENCES `weekly_schedule` (`weekday`);
COMMIT;

/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
