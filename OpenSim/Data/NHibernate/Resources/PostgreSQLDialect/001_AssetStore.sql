CREATE TABLE Assets (
  ID VARCHAR(36) NOT NULL,
  Type SMALLINT DEFAULT NULL,
  Name VARCHAR(64) DEFAULT NULL,
  Description VARCHAR(64) DEFAULT NULL,
  Local BOOLEAN DEFAULT NULL,
  Temporary BOOLEAN DEFAULT NULL,
  Data BYTEA,
  PRIMARY KEY (ID)
);
